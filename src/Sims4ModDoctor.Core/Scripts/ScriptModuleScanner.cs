using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Sims4ModDoctor.Core.Conflicts;
using Sims4ModDoctor.Core.Duplicates;

namespace Sims4ModDoctor.Core.Scripts;

public interface IScriptModuleScanner
{
    Task<ScriptModuleScanReport> ScanAsync(
        ScriptModuleScanRequest request,
        IProgress<ScriptModuleScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 脚本模块碰撞扫描：列出每个 .ts4script 里可 import 的模块，找出出现在两个以上归档里的模块名，
/// 并按内容分成相同 / 不同 / 没比完。
/// </summary>
/// <remarks>
/// <para>只读取 zip 结构与条目字节，不执行、不反编译任何代码。
/// 脚本模块和 package 资源是两套东西，碰撞结果不混入 TGI 模型。</para>
/// <para>每个归档只打开一次：列模块和计算内容哈希在同一次读取里完成，
/// 读取前后核对文件戳，中途被改动则整个归档的结果作废。代价是不碰撞的模块也会被读一遍；
/// 脚本归档普遍很小，这比分两轮读、再在两轮之间核对文件版本更简单可靠。</para>
/// <para>.pyc 开头 16 字节是头部（magic、flags、源文件时间戳与大小或源码哈希，见 PEP 552）。
/// 同一份源码在不同时间编译，头部不同而字节码相同。为了不把这种情况报成「内容不同」，
/// 头部的 magic 结尾是 <c>\r\n</c> 时跳过这 16 字节再算哈希；不像 .pyc 头部的按整份内容算。
/// 因此两个出现「内容相同」表示字节码相同，并不比较编译时间。</para>
/// </remarks>
public sealed class ScriptModuleScanner(IFileSystemAccess fileSystem) : IScriptModuleScanner
{
    public const string ScriptExtension = ".ts4script";

    private const int PycHeaderLength = 16;
    private const int ReadChunkBytes = 64 * 1024;

    private const uint EndOfCentralDirectorySignature = 0x06054B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const int EndOfCentralDirectoryLength = 22;
    private const int Zip64LocatorLength = 20;

    private readonly SourceFileDiscovery _discovery = new(fileSystem);

    public async Task<ScriptModuleScanReport> ScanAsync(
        ScriptModuleScanRequest request,
        IProgress<ScriptModuleScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Sources);
        var limits = request.Limits ?? ScriptModuleLimits.Default;
        limits.Validate();

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScriptModuleScanProgress(ScriptModuleScanPhase.Starting, "正在准备扫描…"));

        var startedAt = DateTime.UtcNow;
        var sourceIssues = new List<ScanIssue>();
        var sources = SourceFileDiscovery.NormalizeSources(request.Sources, sourceIssues);
        var validSources = _discovery.ValidateSources(sources, sourceIssues);
        var sourceSnapshots = SourceFileDiscovery.CreateSourceSnapshots(sources);

        progress?.Report(new ScriptModuleScanProgress(ScriptModuleScanPhase.Discovering, "正在查找脚本文件…"));
        var discovered = _discovery.DiscoverFiles(
            validSources,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ScriptExtension },
            sourceIssues,
            progress is null
                ? null
                : (message, count) => progress.Report(new ScriptModuleScanProgress(
                    ScriptModuleScanPhase.Discovering,
                    message,
                    DiscoveredArchiveCount: count)),
            cancellationToken);

        // 每个结果写回自己在数组里的位置，并发完成顺序不影响结果。
        var archivePaths = discovered.Keys
            .OrderBy(path => path, PathRules.Comparer)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var budget = new ResourceContentBudget(limits.MaxTotalProcessedBytes);
        var outcomes = new ArchiveOutcome[archivePaths.Length];
        var completed = 0;

        progress?.Report(new ScriptModuleScanProgress(
            ScriptModuleScanPhase.ReadingArchives,
            archivePaths.Length == 0 ? "没有需要读取的脚本文件" : "正在读取脚本文件…",
            0,
            archivePaths.Length,
            archivePaths.Length));

        await Parallel.ForEachAsync(
                Enumerable.Range(0, archivePaths.Length),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = limits.MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken,
                },
                async (index, token) =>
                {
                    outcomes[index] = await ReadArchiveSafelyAsync(archivePaths[index], limits, budget, token)
                        .ConfigureAwait(false);

                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(new ScriptModuleScanProgress(
                        ScriptModuleScanPhase.ReadingArchives,
                        $"正在读取脚本文件（{done}/{archivePaths.Length}）",
                        done,
                        archivePaths.Length,
                        archivePaths.Length));
                })
            .ConfigureAwait(false);

        progress?.Report(new ScriptModuleScanProgress(
            ScriptModuleScanPhase.Grouping,
            "正在比对模块名…",
            archivePaths.Length,
            archivePaths.Length,
            archivePaths.Length));

        var issues = sourceIssues.Select(FromScanIssue).ToList();
        var incomplete = new List<string>();
        var occurrences = new List<ScriptModuleOccurrence>();
        for (var index = 0; index < archivePaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = archivePaths[index];
            var outcome = outcomes[index];
            issues.AddRange(outcome.Issues);
            if (outcome.Modules is null)
            {
                incomplete.Add(path);
                continue;
            }

            var sourceIds = SourceIdsFor(path, validSources);
            occurrences.AddRange(outcome.Modules.Select(module => new ScriptModuleOccurrence(
                module.Entry.ModuleName,
                path,
                outcome.Stamp,
                sourceIds,
                module.Entry.EntryName,
                module.Entry.Kind,
                module.Entry.Format,
                module.Sha256,
                module.ContentLength,
                module.Issue)));
        }

        var collisions = BuildCollisions(occurrences, cancellationToken);
        var report = new ScriptModuleScanReport(
            startedAt,
            DateTime.UtcNow,
            ReadOnlyLists.Freeze(sourceSnapshots),
            limits,
            archivePaths.Length,
            archivePaths.Length - incomplete.Count,
            ReadOnlyLists.Freeze(incomplete),
            occurrences.Select(occurrence => occurrence.ModuleName).Distinct(StringComparer.Ordinal).Count(),
            collisions,
            OrderIssues(issues),
            budget.ConsumedBytes);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScriptModuleScanProgress(
            ScriptModuleScanPhase.Completed,
            collisions.Count == 0 ? "扫描完成，没有发现模块碰撞" : $"扫描完成，发现 {collisions.Count} 个模块碰撞",
            archivePaths.Length,
            archivePaths.Length,
            archivePaths.Length));
        return report;
    }

    private async Task<ArchiveOutcome> ReadArchiveSafelyAsync(
        string path,
        ScriptModuleLimits limits,
        ResourceContentBudget budget,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadArchiveAsync(path, limits, budget, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 一个坏文件或一处缺陷只算这一个归档失败，不中断整次扫描；内存不足照常向上抛。
            return ArchiveOutcome.Failed(Issue(
                ScriptArchiveIssueCode.UnexpectedError,
                ScriptArchiveIssueStage.Unexpected,
                path,
                "读取这个脚本文件时出现意外错误，已跳过。",
                detail: Describe(exception)));
        }
    }

    private async Task<ArchiveOutcome> ReadArchiveAsync(
        string path,
        ScriptModuleLimits limits,
        ResourceContentBudget budget,
        CancellationToken cancellationToken)
    {
        FileStamp before;
        Stream stream;
        try
        {
            before = fileSystem.GetFileStamp(path);
            if (before.Length > limits.MaxArchiveBytes)
            {
                return ArchiveOutcome.Failed(Issue(
                    ScriptArchiveIssueCode.TooLarge,
                    ScriptArchiveIssueStage.Structure,
                    path,
                    "脚本文件大得不正常，已跳过。",
                    detail: $"{before.Length} bytes > {limits.MaxArchiveBytes}"));
            }
            stream = fileSystem.OpenRead(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ArchiveOutcome.Failed(Issue(
                ScriptArchiveIssueCode.AccessFailed,
                ScriptArchiveIssueStage.Access,
                path,
                "无法打开这个脚本文件。",
                detail: Describe(exception)));
        }

        List<ModuleResult> modules;
        var issues = new List<ScriptArchiveIssue>();
        await using (stream.ConfigureAwait(false))
        {
            var structureIssue = await PrecheckAsync(stream, path, limits, cancellationToken).ConfigureAwait(false);
            if (structureIssue is not null)
            {
                return ArchiveOutcome.Failed(structureIssue);
            }

            stream.Position = 0;
            ZipArchive archive;
            try
            {
                archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            }
            catch (InvalidDataException exception)
            {
                return ArchiveOutcome.Failed(Issue(
                    ScriptArchiveIssueCode.Corrupt,
                    ScriptArchiveIssueStage.Structure,
                    path,
                    "这个脚本文件不是完好的 zip，无法分析。",
                    detail: Describe(exception)));
            }

            using (archive)
            {
                var selected = SelectModuleEntries(archive.Entries);
                modules = new List<ModuleResult>(selected.Count);
                foreach (var (entry, zipEntry) in selected)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await HashEntryAsync(path, entry, zipEntry, limits, budget, cancellationToken)
                        .ConfigureAwait(false);
                    modules.Add(result);
                    if (result.Issue is not null)
                    {
                        issues.Add(result.Issue);
                    }
                }
            }
        }

        FileStamp after;
        try
        {
            after = fileSystem.GetFileStamp(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ArchiveOutcome.Failed(Issue(
                ScriptArchiveIssueCode.AccessFailed,
                ScriptArchiveIssueStage.Access,
                path,
                "读取后无法再次确认这个脚本文件的状态。",
                detail: Describe(exception)));
        }

        if (after != before)
        {
            // 中途被改过，读到的条目可能来自新旧两个版本，整份作废。
            return ArchiveOutcome.Failed(Issue(
                ScriptArchiveIssueCode.ChangedDuringRead,
                ScriptArchiveIssueStage.Stability,
                path,
                "读取期间这个脚本文件被改动了，结果已作废，请重新扫描。"));
        }

        return new ArchiveOutcome(before, modules, issues);
    }

    /// <summary>
    /// 在构造 <see cref="ZipArchive"/> 之前，从文件尾部读出中央目录尾部记录并检查。
    /// ZipArchive 会一次性把整个中央目录读进内存，条目数必须先在这里卡住。
    /// </summary>
    private static async Task<ScriptArchiveIssue?> PrecheckAsync(
        Stream stream,
        string path,
        ScriptModuleLimits limits,
        CancellationToken cancellationToken)
    {
        var length = stream.Length;
        if (length < EndOfCentralDirectoryLength)
        {
            return NotZip(path, "file shorter than end-of-central-directory record");
        }

        // 尾部记录后面最多跟 65535 字节注释，所以只需要看最后这么长一段。
        var tailLength = (int)Math.Min(length, EndOfCentralDirectoryLength + ushort.MaxValue + Zip64LocatorLength);
        var tail = new byte[tailLength];
        stream.Position = length - tailLength;
        await stream.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);

        return CheckEndOfCentralDirectory(tail, length, path, limits);
    }

    private static ScriptArchiveIssue? CheckEndOfCentralDirectory(
        byte[] tail,
        long length,
        string path,
        ScriptModuleLimits limits)
    {
        var tailLength = tail.Length;
        var eocd = -1;
        for (var offset = tailLength - EndOfCentralDirectoryLength; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset)) == EndOfCentralDirectorySignature)
            {
                eocd = offset;
                break;
            }
        }

        if (eocd < 0)
        {
            return NotZip(path, "end-of-central-directory signature not found");
        }

        // 尾部记录：签名 4、本盘号 2、中央目录所在盘号 2、本盘条目数 2、总条目数 2、目录大小 4、目录偏移 4、注释长度 2。
        var record = tail.AsSpan(eocd, EndOfCentralDirectoryLength);
        var diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        var directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
        var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);

        var hasZip64Locator = eocd >= Zip64LocatorLength
            && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd - Zip64LocatorLength)) == Zip64LocatorSignature;
        if (hasZip64Locator
            || totalEntries == ushort.MaxValue
            || directorySize == uint.MaxValue
            || directoryOffset == uint.MaxValue)
        {
            return Issue(
                ScriptArchiveIssueCode.Zip64Unsupported,
                ScriptArchiveIssueStage.Structure,
                path,
                "这个脚本文件用的是 Zip64 格式，暂不支持分析。");
        }

        if (diskNumber != 0 || directoryDisk != 0 || entriesOnDisk != totalEntries)
        {
            // 分卷 zip。脚本 mod 不会这样打包，ZipArchive 也不支持。
            return Issue(
                ScriptArchiveIssueCode.Corrupt,
                ScriptArchiveIssueStage.Structure,
                path,
                "这个脚本文件不是完好的 zip，无法分析。",
                detail: $"multi-disk archive: disk {diskNumber}, directory disk {directoryDisk}, {entriesOnDisk}/{totalEntries} entries");
        }

        if (totalEntries > limits.MaxEntriesPerArchive)
        {
            return Issue(
                ScriptArchiveIssueCode.EntryCountExceedsLimit,
                ScriptArchiveIssueStage.Structure,
                path,
                "这个脚本文件里的条目多得不正常，已跳过。",
                detail: $"{totalEntries} entries > {limits.MaxEntriesPerArchive}");
        }

        var eocdPosition = length - tailLength + eocd;
        if ((long)directoryOffset + directorySize > eocdPosition)
        {
            return Issue(
                ScriptArchiveIssueCode.Corrupt,
                ScriptArchiveIssueStage.Structure,
                path,
                "这个脚本文件不是完好的 zip，无法分析。",
                detail: $"central directory {directoryOffset}+{directorySize} overlaps end record at {eocdPosition}");
        }

        return null;
    }

    /// <summary>按 zipimport 的查找顺序，每个模块名只保留会被加载的那个条目。</summary>
    private static IReadOnlyList<(ScriptModuleEntry Entry, ZipArchiveEntry ZipEntry)> SelectModuleEntries(
        IEnumerable<ZipArchiveEntry> entries)
    {
        var byModule = new Dictionary<string, (ScriptModuleEntry Entry, ZipArchiveEntry ZipEntry)>(StringComparer.Ordinal);
        foreach (var zipEntry in entries)
        {
            var entry = ScriptModuleNames.TryParse(zipEntry.FullName);
            if (entry is null)
            {
                continue;
            }

            if (!byModule.TryGetValue(entry.ModuleName, out var existing)
                || entry.Priority < existing.Entry.Priority)
            {
                byModule[entry.ModuleName] = (entry, zipEntry);
            }
        }

        return byModule.Values
            .OrderBy(pair => pair.Entry.ModuleName, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<ModuleResult> HashEntryAsync(
        string path,
        ScriptModuleEntry entry,
        ZipArchiveEntry zipEntry,
        ScriptModuleLimits limits,
        ResourceContentBudget budget,
        CancellationToken cancellationToken)
    {
        using var hasher = new ModuleContentHasher(entry.Format == ScriptModuleFormat.Compiled);
        long total = 0;
        var buffer = new byte[ReadChunkBytes];

        try
        {
            await using var content = zipEntry.Open();
            while (true)
            {
                // 先预留再读：额度只按实际输出结算，用不完的退回，并发任务共享同一份。
                var remainingAllowed = limits.MaxModuleBytes - total;
                var want = (int)Math.Min(buffer.Length, remainingAllowed + 1);
                using var reservation = await budget.ReserveUpToAsync(want, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (reservation.GrantedBytes == 0)
                {
                    return ModuleResult.Failed(entry, Issue(
                        ScriptArchiveIssueCode.BudgetExhausted,
                        ScriptArchiveIssueStage.Budget,
                        path,
                        "本次扫描的处理量已达上限，这个模块没有比较。",
                        entryName: entry.EntryName));
                }

                var read = await content.ReadAsync(buffer.AsMemory(0, reservation.GrantedBytes), cancellationToken)
                    .ConfigureAwait(false);
                reservation.Complete(read);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > limits.MaxModuleBytes)
                {
                    return ModuleResult.Failed(entry, Issue(
                        ScriptArchiveIssueCode.EntrySizeExceedsLimit,
                        ScriptArchiveIssueStage.Entry,
                        path,
                        "这个模块解压后大得不正常，没有比较。",
                        entryName: entry.EntryName,
                        detail: $"> {limits.MaxModuleBytes} bytes"));
                }

                hasher.Append(buffer, read);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            // 数据损坏、CRC 不符、不支持的压缩方式、加密：只算这一个模块没比较，归档其余部分照常。
            return ModuleResult.Failed(entry, Issue(
                ScriptArchiveIssueCode.EntryCorrupt,
                ScriptArchiveIssueStage.Entry,
                path,
                "这个模块读不出来，可能已损坏或被加密，没有比较。",
                entryName: entry.EntryName,
                detail: Describe(exception)));
        }

        return new ModuleResult(entry, hasher.Finish(), total, null);
    }

    private static IReadOnlyList<ScriptModuleCollision> BuildCollisions(
        IEnumerable<ScriptModuleOccurrence> occurrences,
        CancellationToken cancellationToken) =>
        ReadOnlyLists.Freeze(occurrences
            .GroupBy(occurrence => occurrence.ModuleName, StringComparer.Ordinal)
            .Where(group => group.Select(occurrence => occurrence.ArchivePath).Distinct(PathRules.Comparer).Skip(1).Any())
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ordered = group
                    .OrderBy(occurrence => occurrence.ArchivePath, PathRules.Comparer)
                    .ThenBy(occurrence => occurrence.ArchivePath, StringComparer.Ordinal)
                    .ToArray();
                var contentGroups = ordered
                    .Where(occurrence => occurrence.IsSuccess)
                    .GroupBy(occurrence => occurrence.Sha256!, StringComparer.Ordinal)
                    .Select(byHash => new ScriptModuleContentGroup(byHash.Key, ReadOnlyLists.Freeze(byHash)))
                    .OrderByDescending(byHash => byHash.Occurrences.Count)
                    .ThenBy(byHash => byHash.Occurrences[0].ArchivePath, PathRules.Comparer)
                    .ThenBy(byHash => byHash.Sha256, StringComparer.Ordinal);
                return new ScriptModuleCollision(
                    group.Key,
                    ReadOnlyLists.Freeze(contentGroups),
                    ReadOnlyLists.Freeze(ordered.Where(occurrence => !occurrence.IsSuccess)));
            }));

    private static IReadOnlyList<string> SourceIdsFor(string path, IReadOnlyList<NormalizedSource> sources) =>
        ReadOnlyLists.Freeze(sources
            .Where(source => PathRules.IsWithin(path, source.Path))
            .OrderBy(source => source.Order)
            .ThenBy(source => source.InputIndex)
            .Select(source => source.Id));

    private static ScriptArchiveIssue FromScanIssue(ScanIssue issue) => new(
        issue.Code,
        issue.Stage switch
        {
            ScanIssueStage.SourceValidation => ScriptArchiveIssueStage.SourceValidation,
            ScanIssueStage.Discovery => ScriptArchiveIssueStage.Discovery,
            ScanIssueStage.Metadata => ScriptArchiveIssueStage.Metadata,
            _ => throw new ArgumentOutOfRangeException(nameof(issue), issue.Stage, "发现阶段不会产生这一类问题。"),
        },
        issue.Path,
        issue.Message,
        issue.SourceId);

    private static IReadOnlyList<ScriptArchiveIssue> OrderIssues(IEnumerable<ScriptArchiveIssue> issues) =>
        ReadOnlyLists.Freeze(issues
            .OrderBy(issue => issue.Stage)
            .ThenBy(issue => issue.Path, PathRules.Comparer)
            .ThenBy(issue => issue.Path, StringComparer.Ordinal)
            .ThenBy(issue => issue.EntryName, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal));

    private static ScriptArchiveIssue NotZip(string path, string detail) => Issue(
        ScriptArchiveIssueCode.NotZip,
        ScriptArchiveIssueStage.Structure,
        path,
        "这个文件不是 zip，不像正常的脚本 mod。",
        detail: detail);

    private static ScriptArchiveIssue Issue(
        string code,
        ScriptArchiveIssueStage stage,
        string path,
        string message,
        string? entryName = null,
        string? detail = null) =>
        new(code, stage, path, message, EntryName: entryName, Detail: detail);

    private static string Describe(Exception exception) => $"{exception.GetType().FullName}: {exception.Message}";

    /// <summary>
    /// 流式哈希。.pyc 先攒满 16 字节头部：magic 结尾是 \r\n 就丢掉头部，否则头部也算进哈希。
    /// 不足 16 字节的内容整份参与哈希。
    /// </summary>
    private sealed class ModuleContentHasher(bool isCompiled) : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _header = new byte[PycHeaderLength];
        private int _headerFilled;
        private bool _headerDecided = !isCompiled;

        public void Append(byte[] buffer, int count)
        {
            var chunk = buffer.AsSpan(0, count);
            if (!_headerDecided)
            {
                var take = Math.Min(PycHeaderLength - _headerFilled, chunk.Length);
                chunk[..take].CopyTo(_header.AsSpan(_headerFilled));
                _headerFilled += take;
                chunk = chunk[take..];
                if (_headerFilled == PycHeaderLength)
                {
                    _headerDecided = true;
                    if (!(_header[2] == 0x0D && _header[3] == 0x0A))
                    {
                        _hash.AppendData(_header);
                    }
                }
            }

            if (!chunk.IsEmpty)
            {
                _hash.AppendData(chunk);
            }
        }

        public string Finish()
        {
            if (!_headerDecided)
            {
                _hash.AppendData(_header.AsSpan(0, _headerFilled));
            }
            return Convert.ToHexString(_hash.GetHashAndReset());
        }

        public void Dispose() => _hash.Dispose();
    }

    private sealed record ModuleResult(ScriptModuleEntry Entry, string? Sha256, long? ContentLength, ScriptArchiveIssue? Issue)
    {
        public static ModuleResult Failed(ScriptModuleEntry entry, ScriptArchiveIssue issue) => new(entry, null, null, issue);
    }

    private sealed record ArchiveOutcome(
        FileStamp Stamp,
        IReadOnlyList<ModuleResult>? Modules,
        IReadOnlyList<ScriptArchiveIssue> Issues)
    {
        public static ArchiveOutcome Failed(ScriptArchiveIssue issue) => new(default, null, [issue]);
    }
}
