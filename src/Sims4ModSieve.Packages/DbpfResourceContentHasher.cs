using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Zip.Compression;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;

namespace Sims4ModSieve.Packages;

/// <summary>
/// <see cref="IResourceContentHasher"/> 的实现：按 DBPF 索引定位资源，只读取那一段存储数据，
/// 流式解压并增量计算 SHA-256。
/// </summary>
/// <remarks>
/// 为什么不用第三方库取内容：锁定版本的 LlamaLogic.Packages 3.8.2 在取资源时
/// <list type="bullet">
/// <item>按索引声明的解压大小直接分配整块内存（最大可声明到 4 GiB），而不是按实际输出；</item>
/// <item>zlib 只调用一次 <c>Read</c> 就返回，读不满时静默返回前半截内容，不校验长度；</item>
/// <item>RefPack 同样按头部声明大小预分配，回引用不做越界检查，不支持取消，结尾不核对长度；</item>
/// <item>没有公开的流式读取入口。</item>
/// </list>
/// 这些行为都无法在调用前用声明值完全防住，所以内容读取由本类直接完成；
/// 索引读取仍由 <see cref="LlamaLogicPackageIndexReader"/> 负责，本类只按同一布局复核目标记录。
/// <para>
/// 支持边界：未压缩与 zlib。RefPack（Maxis 内部压缩，含 streamable 变体）本轮返回
/// <see cref="ResourceContentIssueCode.UnsupportedCompression"/>，不调用无界解压。
/// </para>
/// </remarks>
public sealed class DbpfResourceContentHasher(
    IFileSystemAccess fileSystem,
    PackageSafetyLimits? packageLimits = null) : IResourceContentHasher
{
    private const int ChunkSize = 64 * 1024;

    private const ushort CompressionNone = 0x0000;
    private const ushort CompressionZlib = 0x5A42;
    private const ushort CompressionDeleted = 0xFFE0;
    private const ushort CompressionStreamable = 0xFFFE;
    private const ushort CompressionInternal = 0xFFFF;

    private const uint ExtendedCompressionFlag = 0x8000_0000;
    private const uint SizeMask = 0x7FFF_FFFF;

    private readonly PackageSafetyLimits _packageLimits = packageLimits ?? PackageSafetyLimits.Default;

    public static DbpfResourceContentHasher CreateDefault() => new(new PhysicalFileSystemAccess());

    public async Task<ResourceContentBatchResult> HashAsync(
        ResourceContentBatchRequest request,
        ResourceContentBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Targets);
        ArgumentNullException.ThrowIfNull(request.Limits);
        ArgumentNullException.ThrowIfNull(budget);
        request.Limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var path = request.PackagePath;
        var targets = request.Targets;

        var stampBefore = TryGetStamp(path, out var accessIssue);
        if (stampBefore is null)
        {
            return new ResourceContentBatchResult(path, null, FailAll(targets, accessIssue!));
        }

        if (stampBefore.Value != request.ExpectedStamp)
        {
            return new ResourceContentBatchResult(path, stampBefore, FailAll(targets, ChangedSinceScan(request.ExpectedStamp, stampBefore.Value)));
        }

        var results = new ResourceContentResult[targets.Count];
        try
        {
            await using var stream = fileSystem.OpenRead(path);

            var locateIssue = await LocateAsync(stream, stampBefore.Value.Length, targets, cancellationToken)
                .ConfigureAwait(false);
            if (locateIssue.Issue is not null)
            {
                return await FinishAsync(path, request.ExpectedStamp, FailAll(targets, locateIssue.Issue)).ConfigureAwait(false);
            }

            // 按文件内位置读取，顺着磁盘走；结果写回请求里的位置，顺序与读取顺序无关。
            var order = Enumerable.Range(0, targets.Count)
                .OrderBy(index => locateIssue.Entries[index]?.Position ?? 0)
                .ThenBy(index => index)
                .ToArray();

            foreach (var index in order)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = targets[index];
                var entry = locateIssue.Entries[index];
                results[index] = entry is null
                    ? ResourceContentResult.Failure(target, IndexMismatch(target, "索引里没有这个位置的资源。"))
                    : await HashOneAsync(stream, stampBefore.Value.Length, target, entry.Value, request.Limits, budget, cancellationToken)
                        .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 打开或定位阶段的 IO 失败：这一批都没读成。单条资源读取中的 IO 失败在 HashOneAsync 里单独处理。
            return await FinishAsync(path, request.ExpectedStamp, FailAll(targets, new ResourceContentIssue(
                ResourceContentIssueCode.AccessFailed,
                ResourceContentStage.Access,
                "无法读取这个 package。",
                $"{exception.GetType().Name}: {exception.Message}"))).ConfigureAwait(false);
        }

        return await FinishAsync(path, request.ExpectedStamp, results).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取结束后再取一次文件戳。读取期间文件被改过，这一批的哈希全部作废——
    /// 同一个 package 的资源不能混用不同文件版本算出的哈希。
    /// </summary>
    /// <remarks>
    /// 文件戳只是变化检测：同大小、同修改时间的改写不一定能发现。
    /// </remarks>
    private Task<ResourceContentBatchResult> FinishAsync(
        string path,
        FileStamp expected,
        IReadOnlyList<ResourceContentResult> results)
    {
        var after = TryGetStamp(path, out var accessIssue);
        if (after is null)
        {
            return Task.FromResult(new ResourceContentBatchResult(
                path,
                null,
                Replace(results, new ResourceContentIssue(
                    ResourceContentIssueCode.ChangedDuringRead,
                    ResourceContentStage.Stability,
                    "读取结束后无法确认这个文件没有被改动，本次结果已作废。",
                    accessIssue!.Detail))));
        }

        if (after.Value != expected)
        {
            return Task.FromResult(new ResourceContentBatchResult(
                path,
                after,
                Replace(results, new ResourceContentIssue(
                    ResourceContentIssueCode.ChangedDuringRead,
                    ResourceContentStage.Stability,
                    "这个文件在读取过程中被改动了，本次结果已作废。",
                    $"期望 {Describe(expected)}，读取后 {Describe(after.Value)}。"))));
        }

        return Task.FromResult(new ResourceContentBatchResult(path, after, results));
    }

    private static IReadOnlyList<ResourceContentResult> Replace(
        IReadOnlyList<ResourceContentResult> results,
        ResourceContentIssue issue) =>
        results.Select(result => ResourceContentResult.Failure(result.Target, issue)).ToArray();

    private static IReadOnlyList<ResourceContentResult> FailAll(
        IReadOnlyList<ResourceContentTarget> targets,
        ResourceContentIssue issue) =>
        targets.Select(target => ResourceContentResult.Failure(target, issue)).ToArray();

    private FileStamp? TryGetStamp(string path, out ResourceContentIssue? issue)
    {
        issue = null;
        try
        {
            return fileSystem.GetFileStamp(path);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            issue = new ResourceContentIssue(
                ResourceContentIssueCode.AccessFailed,
                ResourceContentStage.Access,
                "无法读取这个 package。",
                $"{exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    // ---------- 索引定位 ----------

    private readonly record struct IndexEntry(
        ResourceKey Key,
        uint Position,
        uint StoredSize,
        uint DeclaredSize,
        ushort Compression);

    private sealed record LocateResult(ResourceContentIssue? Issue, IndexEntry?[] Entries);

    /// <summary>
    /// 复核 header，顺着索引走到每个目标 ordinal，取出它的位置、大小与压缩方式。
    /// 布局与 LlamaLogic 3.8.2 <c>DataBasePackedFile.ParseIndex</c> 逐字段一致：
    /// 位域 → 公共常量 → 每条 [Type][Group][InstanceHi][InstanceLo][Position][Size][MemSize]
    /// （公共常量对应的字段不逐条存储），Size 最高位置位时尾随 [Compression u16][Committed u16]。
    /// 没有扩展字段的记录按未压缩处理，与该库相同。
    /// </summary>
    private async Task<LocateResult> LocateAsync(
        Stream stream,
        long fileLength,
        IReadOnlyList<ResourceContentTarget> targets,
        CancellationToken cancellationToken)
    {
        var entries = new IndexEntry?[targets.Count];
        if (targets.Count == 0)
        {
            return new LocateResult(null, entries);
        }

        var header = new byte[DbpfPrecheck.HeaderLength];
        try
        {
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return new LocateResult(IndexUnreadable("文件比 header 还短。"), entries);
        }

        var headerIssue = DbpfPrecheck.ValidateHeader(header, fileLength, string.Empty, _packageLimits, out var parsed);
        if (headerIssue is not null)
        {
            return new LocateResult(IndexUnreadable($"{headerIssue.Code}: {headerIssue.Detail ?? headerIssue.Message}"), entries);
        }

        if (parsed.EntryCount == 0)
        {
            return new LocateResult(null, entries);
        }

        // 索引大小已由 ValidateHeader 限制在文件范围内；再按位域核对一次形状，
        // 保证下面逐条读取不会越过索引末尾。
        var wanted = new Dictionary<int, List<int>>();
        for (var index = 0; index < targets.Count; index++)
        {
            var ordinal = targets[index].Ordinal;
            if (ordinal < 0 || ordinal >= parsed.EntryCount)
            {
                continue;
            }

            if (!wanted.TryGetValue(ordinal, out var slots))
            {
                slots = [];
                wanted.Add(ordinal, slots);
            }

            slots.Add(index);
        }

        if (wanted.Count == 0)
        {
            return new LocateResult(null, entries);
        }

        var lastWanted = wanted.Keys.Max();

        stream.Seek((long)parsed.IndexPosition, SeekOrigin.Begin);
        var reader = new IndexCursor(stream, parsed.IndexSize);
        try
        {
            var indexType = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
            var shapeIssue = DbpfPrecheck.ValidateIndexShape(indexType, parsed.EntryCount, parsed.IndexSize, string.Empty);
            if (shapeIssue is not null)
            {
                return new LocateResult(IndexUnreadable($"{shapeIssue.Code}: {shapeIssue.Detail ?? shapeIssue.Message}"), entries);
            }

            uint? constType = (indexType & 0x01) != 0 ? await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false) : null;
            uint? constGroup = (indexType & 0x02) != 0 ? await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false) : null;
            uint? constInstanceHi = (indexType & 0x04) != 0 ? await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false) : null;

            for (var ordinal = 0; ordinal <= lastWanted; ordinal++)
            {
                if ((ordinal & 0x3FF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var type = constType ?? await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                var group = constGroup ?? await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                var instanceHi = constInstanceHi ?? await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                var instanceLo = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                var position = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                var size = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                var memSize = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                var compression = CompressionNone;
                if ((size & ExtendedCompressionFlag) != 0)
                {
                    var extended = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                    compression = (ushort)(extended & 0xFFFF);
                }

                if (wanted.TryGetValue(ordinal, out var slots))
                {
                    var entry = new IndexEntry(
                        new ResourceKey(type, group, ((ulong)instanceHi << 32) | instanceLo),
                        position,
                        size & SizeMask,
                        memSize,
                        compression);
                    foreach (var slot in slots)
                    {
                        entries[slot] = entry;
                    }
                }
            }
        }
        catch (EndOfStreamException)
        {
            return new LocateResult(IndexUnreadable("索引比声明的短，文件可能已被截断。"), entries);
        }

        return new LocateResult(null, entries);
    }

    /// <summary>顺序读取索引的小游标，读取总量不超过索引声明的长度。</summary>
    private sealed class IndexCursor(Stream stream, uint indexSize)
    {
        private readonly byte[] _buffer = new byte[sizeof(uint)];
        private long _remaining = indexSize;

        public async ValueTask<uint> ReadUInt32Async(CancellationToken cancellationToken)
        {
            if (_remaining < sizeof(uint))
            {
                throw new EndOfStreamException();
            }

            await stream.ReadExactlyAsync(_buffer, cancellationToken).ConfigureAwait(false);
            _remaining -= sizeof(uint);
            return BinaryPrimitives.ReadUInt32LittleEndian(_buffer);
        }
    }

    // ---------- 单条资源 ----------

    private static async Task<ResourceContentResult> HashOneAsync(
        Stream stream,
        long fileLength,
        ResourceContentTarget target,
        IndexEntry entry,
        ResourceContentLimits limits,
        ResourceContentBudget budget,
        CancellationToken cancellationToken)
    {
        // 不能只凭 TGI 找资源：ordinal 处的键必须与候选扫描时一致，否则说明索引已经不是那一份。
        if (entry.Key != target.Key)
        {
            return ResourceContentResult.Failure(target, IndexMismatch(
                target,
                $"位置 {target.Ordinal} 上的资源键是 {entry.Key.Tgi}，候选扫描时是 {target.Key.Tgi}。"));
        }

        switch (entry.Compression)
        {
            case CompressionNone:
            case CompressionZlib:
                break;
            case CompressionDeleted:
                return Fail(target, ResourceContentIssueCode.Deleted, ResourceContentStage.Decompress,
                    "这条资源在 package 里被标记为已删除，没有可比较的内容。");
            case CompressionInternal:
            case CompressionStreamable:
                return Fail(target, ResourceContentIssueCode.UnsupportedCompression, ResourceContentStage.Decompress,
                    "这条资源使用 Maxis 内部压缩（RefPack），本版本暂不支持比较它的内容。",
                    $"compression 0x{entry.Compression:X4}");
            default:
                return Fail(target, ResourceContentIssueCode.UnsupportedCompression, ResourceContentStage.Decompress,
                    "这条资源使用了不认识的压缩方式，无法比较内容。",
                    $"compression 0x{entry.Compression:X4}");
        }

        // 声明值只用于提前拒绝。
        if (entry.StoredSize > limits.MaxStoredBytesPerResource)
        {
            return Fail(target, ResourceContentIssueCode.StoredSizeExceedsLimit, ResourceContentStage.Read,
                "这条资源的存储数据超出了单条读取上限。",
                $"声明 {entry.StoredSize} 字节，上限 {limits.MaxStoredBytesPerResource} 字节。");
        }

        if (entry.DeclaredSize > limits.MaxDecompressedBytesPerResource)
        {
            return Fail(target, ResourceContentIssueCode.DecompressedSizeExceedsLimit, ResourceContentStage.Decompress,
                "这条资源解压后的大小超出了单条上限。",
                $"声明 {entry.DeclaredSize} 字节，上限 {limits.MaxDecompressedBytesPerResource} 字节。");
        }

        if (entry.Compression == CompressionNone && entry.StoredSize != entry.DeclaredSize)
        {
            return Fail(target, ResourceContentIssueCode.LengthMismatch, ResourceContentStage.Read,
                "这条未压缩资源的两个长度字段对不上，文件可能已损坏。",
                $"存储 {entry.StoredSize} 字节，声明内容 {entry.DeclaredSize} 字节。");
        }

        var end = (ulong)entry.Position + entry.StoredSize;
        if (entry.Position < DbpfPrecheck.HeaderLength || end > (ulong)fileLength)
        {
            return Fail(target, ResourceContentIssueCode.OutOfBounds, ResourceContentStage.Read,
                "这条资源的位置超出了文件范围，文件可能已损坏或被截断。",
                $"位置 {entry.Position}、长度 {entry.StoredSize}，文件长度 {fileLength}。");
        }

        try
        {
            stream.Seek(entry.Position, SeekOrigin.Begin);
            using var stored = new BoundedReadStream(stream, entry.StoredSize, budget, cancellationToken,
                entry.Compression == CompressionNone ? 2 : 1);
            return entry.Compression == CompressionNone
                ? await HashStoredAsync(stored, target, entry, cancellationToken).ConfigureAwait(false)
                : await HashZlibAsync(stored, target, entry, limits, budget, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BudgetExhaustedException)
        {
            return Fail(target, ResourceContentIssueCode.BudgetExhausted, ResourceContentStage.Budget,
                "这次比较的处理量已经达到上限，剩下的资源没有比较。",
                $"总预算 {budget.TotalBytes} 字节。");
        }
        catch (EndOfStreamException)
        {
            return Fail(target, ResourceContentIssueCode.Truncated, ResourceContentStage.Read,
                "这条资源的数据比索引声明的短，文件可能已被截断。");
        }
        catch (Exception exception) when (exception is InvalidDataException or SharpZipBaseException)
        {
            return Fail(target, ResourceContentIssueCode.Corrupt, ResourceContentStage.Decompress,
                "这条资源的压缩数据已损坏，无法解压。",
                exception.Message);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Fail(target, ResourceContentIssueCode.AccessFailed, ResourceContentStage.Read,
                "读取这条资源时出错。",
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>未压缩：存储数据就是内容。读满声明长度才算成功。</summary>
    private static async Task<ResourceContentResult> HashStoredAsync(
        BoundedReadStream stored,
        ResourceContentTarget target,
        IndexEntry entry,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            long total = 0;
            int read;
            while ((read = await stored.ReadAsync(buffer.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                total += read;
            }

            if (total != entry.StoredSize)
            {
                throw new EndOfStreamException();
            }

            return ResourceContentResult.Success(target, Convert.ToHexString(hash.GetHashAndReset()), total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// zlib：按预算分块解压与哈希，由 Inflater 校验结束块与 Adler-32。
    /// </summary>
    /// <remarks>
    /// 成功要求 IsFinished、输入恰好耗尽且输出长度匹配；底层 EOF 不代表解压完成。
    /// 输出一旦超过声明长度或单条上限立即停止，不会先解压完再判断。
    /// </remarks>
    private static async Task<ResourceContentResult> HashZlibAsync(
        BoundedReadStream stored,
        ResourceContentTarget target,
        IndexEntry entry,
        ResourceContentLimits limits,
        ResourceContentBudget budget,
        CancellationToken cancellationToken)
    {
        if (entry.StoredSize < 6)
        {
            return Fail(target, ResourceContentIssueCode.Corrupt, ResourceContentStage.Decompress,
                "这条资源的压缩数据已损坏，无法解压。",
                $"zlib 数据只有 {entry.StoredSize} 字节，不足以容纳头部与校验和。");
        }

        var cap = Math.Min(entry.DeclaredSize, limits.MaxDecompressedBytesPerResource);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var input = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var inflater = new Inflater();
        try
        {
            long total = 0;
            var zeroOutputSteps = 0;
            while (!inflater.IsFinished)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = (int)Math.Min(ChunkSize, cap - total + 1);
                int read;
                int granted;
                var inputBefore = inflater.TotalIn;
                // 预留输出额度时不读取底层流，避免输出预留抢占输入所需的共享额度。
                using (var reservation = await budget.ReserveUpToAsync(request, cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
                {
                    granted = reservation.GrantedBytes;
                    if (granted == 0 && total < entry.DeclaredSize)
                    {
                        throw new BudgetExhaustedException();
                    }
                    var outputBefore = inflater.TotalOut;
                    try
                    {
                        read = inflater.Inflate(buffer, 0, granted);
                    }
                    finally
                    {
                        // 即使本次调用在校验和处抛错，也结算已交付的输出。
                        reservation.Complete(checked((int)(inflater.TotalOut - outputBefore)));
                    }
                }

                total += read;
                if (total > cap)
                {
                    return Fail(target, ResourceContentIssueCode.LengthMismatch, ResourceContentStage.Decompress,
                        "这条资源解压出来的内容比声明的长，已停止解压。",
                        $"声明 {entry.DeclaredSize} 字节，实际输出已超过。");
                }

                if (read > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    zeroOutputSteps = 0;
                    continue;
                }

                if (inflater.IsFinished) break;
                if (inflater.IsNeedingDictionary)
                {
                    throw new InvalidDataException("Preset zlib dictionaries are not supported.");
                }

                if (inflater.IsNeedingInput)
                {
                    var count = await stored.ReadAsync(input.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException();
                    inflater.SetInput(input, 0, count);
                    zeroOutputSteps = 0;
                    continue;
                }

                if (granted == 0)
                {
                    // 预算恰好用满时允许无输出的结束块/校验和状态转换。
                    // 无进展表示仍有待输出内容，不能绕过预算取一个探测字节。
                    zeroOutputSteps = inflater.TotalIn != inputBefore ? 0 : zeroOutputSteps + 1;
                    if (zeroOutputSteps < 4) continue;
                    throw new BudgetExhaustedException();
                }

                throw new InvalidDataException("Inflater made no progress before stream end.");
            }

            if (total != entry.DeclaredSize)
            {
                return Fail(target, ResourceContentIssueCode.LengthMismatch, ResourceContentStage.Decompress,
                    "这条资源解压出来的内容比声明的短，数据可能被截断或损坏。",
                    $"声明 {entry.DeclaredSize} 字节，实际 {total} 字节。");
            }

            if (inflater.RemainingInput != 0 || stored.Consumed != entry.StoredSize)
            {
                return Fail(target, ResourceContentIssueCode.Corrupt, ResourceContentStage.Decompress,
                    "这条资源的压缩流结束后仍有多余数据。");
            }

            return ResourceContentResult.Success(target, Convert.ToHexString(hash.GetHashAndReset()), total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(input);
        }
    }

    private static ResourceContentResult Fail(
        ResourceContentTarget target,
        string code,
        ResourceContentStage stage,
        string message,
        string? detail = null) =>
        ResourceContentResult.Failure(target, new ResourceContentIssue(code, stage, message, detail));

    private static ResourceContentIssue IndexMismatch(ResourceContentTarget target, string detail) => new(
        ResourceContentIssueCode.IndexMismatch,
        ResourceContentStage.Index,
        "这条资源与候选扫描时的索引对不上，没有比较。",
        $"{target.Key.Tgi} @ {target.Ordinal}：{detail}");

    private static ResourceContentIssue IndexUnreadable(string detail) => new(
        ResourceContentIssueCode.IndexUnreadable,
        ResourceContentStage.Index,
        "这个 package 的索引读不出来，没有比较其中的资源。",
        detail);

    private static ResourceContentIssue ChangedSinceScan(FileStamp expected, FileStamp actual) => new(
        ResourceContentIssueCode.ChangedSinceScan,
        ResourceContentStage.Stability,
        "这个文件在候选扫描之后被改动了，需要重新扫描。",
        $"扫描时 {Describe(expected)}，现在 {Describe(actual)}。");

    private static string Describe(FileStamp stamp) => $"{stamp.Length} 字节 / {stamp.LastWriteTimeUtc:O}";

    private sealed class BudgetExhaustedException : Exception;

    /// <summary>
    /// 只读、只前进的子流：最多读 length 字节，读取前预留预算，按实际读取量结算。
    /// </summary>
    private sealed class BoundedReadStream(
        Stream inner,
        long length,
        ResourceContentBudget budget,
        CancellationToken cancellationToken,
        int bytesPerUnit = 1) : Stream
    {
        public long Consumed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => Consumed;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = (int)Math.Min(buffer.Length, length - Consumed);
            if (want <= 0)
            {
                return 0;
            }

            using var reservation = budget.ReserveUpTo(want, bytesPerUnit);
            if (reservation.GrantedBytes == 0)
            {
                throw new BudgetExhaustedException();
            }

            var read = inner.Read(buffer[..reservation.GrantedBytes]);
            reservation.Complete(read);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            Consumed += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, token);
            linked.Token.ThrowIfCancellationRequested();
            var want = (int)Math.Min(buffer.Length, length - Consumed);
            if (want <= 0)
            {
                return 0;
            }

            using var reservation = await budget.ReserveUpToAsync(want, bytesPerUnit, linked.Token).ConfigureAwait(false);
            if (reservation.GrantedBytes == 0)
            {
                throw new BudgetExhaustedException();
            }

            var read = await inner.ReadAsync(buffer[..reservation.GrantedBytes], linked.Token).ConfigureAwait(false);
            reservation.Complete(read);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            Consumed += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}
