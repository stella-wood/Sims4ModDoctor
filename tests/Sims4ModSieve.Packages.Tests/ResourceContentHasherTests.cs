using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Core.Tests.Packages;

namespace Sims4ModSieve.Packages.Tests;

/// <summary>
/// 资源内容读取接上真实文件，跑自造的 DBPF fixture。不使用任何真实玩家 Mod，
/// 不构造大到需要真实耗尽内存的数据：上限都用小配置触发。
/// </summary>
[TestClass]
public sealed class ResourceContentHasherTests
{
    private const uint Type = 0x0904DF10;
    private static readonly byte[] Content = MakeContent(3000);

    [TestMethod]
    public async Task UncompressedAndZlibCopiesOfTheSameContentHashTheSame()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("a.package", new DbpfFixtureBuilder()
            .AddContent(Content)
            .AddContent(DbpfFixtureBuilder.Zlib(Content), DbpfFixtureBuilder.CompressionZlib, (uint)Content.Length)
            .AddContent(DbpfFixtureBuilder.Zlib(Content, CompressionLevel.Fastest), DbpfFixtureBuilder.CompressionZlib, (uint)Content.Length)
            .Build());

        var batch = await HashAsync(path, [0, 1, 2]);

        var expected = Convert.ToHexString(SHA256.HashData(Content));
        foreach (var result in batch.Results)
        {
            Assert.IsTrue(result.IsSuccess, result.Issue?.Code);
            Assert.AreEqual(expected, result.Sha256);
            Assert.AreEqual(Content.Length, result.DecompressedLength);
        }
    }

    [TestMethod]
    public async Task DifferentContentHashesDifferently()
    {
        using var temp = new TempDirectory();
        var other = (byte[])Content.Clone();
        other[^1] ^= 0xFF;
        var path = temp.WriteBytes("a.package", new DbpfFixtureBuilder()
            .AddContent(Content)
            .AddContent(DbpfFixtureBuilder.Zlib(other), DbpfFixtureBuilder.CompressionZlib, (uint)other.Length)
            .Build());

        var batch = await HashAsync(path, [0, 1]);

        Assert.IsTrue(batch.Results.All(result => result.IsSuccess));
        Assert.AreNotEqual(batch.Results[0].Sha256, batch.Results[1].Sha256);
    }

    [TestMethod]
    public async Task DeletedAndUnknownAndRefPackCompressionAreStructuredFailures()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("a.package", new DbpfFixtureBuilder()
            .AddContent([], DbpfFixtureBuilder.CompressionDeleted, 0)
            .AddContent(Content, 0x1234, (uint)Content.Length)
            .AddContent(Content, DbpfFixtureBuilder.CompressionInternal, (uint)Content.Length)
            .AddContent(Content)
            .Build());

        var batch = await HashAsync(path, [0, 1, 2, 3]);

        AssertFailure(batch.Results[0], ResourceContentIssueCode.Deleted);
        AssertFailure(batch.Results[1], ResourceContentIssueCode.UnsupportedCompression);
        AssertFailure(batch.Results[2], ResourceContentIssueCode.UnsupportedCompression);
        Assert.IsTrue(batch.Results[3].IsSuccess, "单条失败不能拖累同一个 package 里的其他资源。");
    }

    [TestMethod]
    public async Task CorruptZlibDataIsReportedNotHashed()
    {
        using var temp = new TempDirectory();
        var compressed = DbpfFixtureBuilder.Zlib(Content);
        for (var index = 2; index < compressed.Length - 4; index += 7)
        {
            compressed[index] ^= 0x5A;
        }

        var path = temp.WriteBytes("a.package", new DbpfFixtureBuilder()
            .AddContent(compressed, DbpfFixtureBuilder.CompressionZlib, (uint)Content.Length)
            .Build());

        var batch = await HashAsync(path, [0]);

        Assert.IsFalse(batch.Results[0].IsSuccess);
        Assert.IsNull(batch.Results[0].Sha256);
        CollectionAssert.Contains(
            new[] { ResourceContentIssueCode.Corrupt, ResourceContentIssueCode.LengthMismatch },
            batch.Results[0].Issue!.Code);
    }

    [TestMethod]
    public async Task WrongAdler32TrailerIsCorrupt()
    {
        using var temp = new TempDirectory();
        var compressed = DbpfFixtureBuilder.Zlib(Content);
        compressed[^1] ^= 0x01;
        var path = temp.WriteBytes("a.package", new DbpfFixtureBuilder()
            .AddContent(compressed, DbpfFixtureBuilder.CompressionZlib, (uint)Content.Length)
            .Build());

        var batch = await HashAsync(path, [0]);

        AssertFailure(batch.Results[0], ResourceContentIssueCode.Corrupt);
    }

    [TestMethod]
    public async Task TruncatedZlibStreamIsNotHashedAsPartialContent()
    {
        using var temp = new TempDirectory();
        var compressed = DbpfFixtureBuilder.Zlib(Content);
        var path = temp.WriteBytes("a.package", new DbpfFixtureBuilder()
            .AddContent(compressed[..(compressed.Length / 2)], DbpfFixtureBuilder.CompressionZlib, (uint)Content.Length)
            .Build());

        var batch = await HashAsync(path, [0]);

        Assert.IsFalse(batch.Results[0].IsSuccess);
        Assert.IsNull(batch.Results[0].Sha256);
    }

    [TestMethod]
    public async Task TruncatedPackageFailsEveryTargetInsteadOfHashingPartially()
    {
        using var temp = new TempDirectory();
        var bytes = new DbpfFixtureBuilder().AddContent(Content).AddContent(Content).Build();
        var path = temp.WriteBytes("a.package", DbpfFixtureBuilder.Truncate(bytes, bytes.Length - 10));

        var batch = await HashAsync(path, [0, 1]);

        Assert.IsTrue(batch.Results.All(result => !result.IsSuccess && result.Sha256 is null));
    }

    [TestMethod]
    public async Task DeclaredSizeLargerThanActualOutputIsLengthMismatch()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("a.package", new DbpfFixtureBuilder()
            .AddContent(DbpfFixtureBuilder.Zlib(Content), DbpfFixtureBuilder.CompressionZlib, (uint)Content.Length + 1)
            .AddContent(Content, DbpfFixtureBuilder.CompressionNone, (uint)Content.Length + 1)
            .Build());

        var batch = await HashAsync(path, [0, 1]);

        AssertFailure(batch.Results[0], ResourceContentIssueCode.LengthMismatch);
        AssertFailure(batch.Results[1], ResourceContentIssueCode.LengthMismatch);
    }

    [TestMethod]
    public async Task SmallDeclaredSizeDoesNotLetLargeOutputThrough()
    {
        // 声明 16 字节，实际解压出 3000 字节：输出一过声明就停，不会先解压完再判断。
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("a.package", new DbpfFixtureBuilder()
            .AddContent(DbpfFixtureBuilder.Zlib(Content), DbpfFixtureBuilder.CompressionZlib, 16)
            .Build());

        var batch = await HashAsync(path, [0]);

        AssertFailure(batch.Results[0], ResourceContentIssueCode.LengthMismatch);
    }

    [TestMethod]
    public async Task PerResourceLimitsRejectByDeclaredSizeBeforeReading()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("a.package", new DbpfFixtureBuilder()
            .AddContent(Content)
            .AddContent(DbpfFixtureBuilder.Zlib(new byte[4096]), DbpfFixtureBuilder.CompressionZlib, 4096)
            .Build());
        var budget = new ResourceContentBudget(1 << 20);

        var batch = await HashAsync(path, [0, 1], new ResourceContentLimits(
            MaxStoredBytesPerResource: 1024,
            MaxDecompressedBytesPerResource: 2048), budget);

        AssertFailure(batch.Results[0], ResourceContentIssueCode.StoredSizeExceedsLimit);
        AssertFailure(batch.Results[1], ResourceContentIssueCode.DecompressedSizeExceedsLimit);
        Assert.AreEqual(0, budget.ConsumedBytes, "按声明值拒绝时不应读取任何数据。");
    }

    [TestMethod]
    public async Task SharedBudgetStopsLaterResourcesWithoutFailingEarlierOnes()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("a.package", new DbpfFixtureBuilder()
            .AddContent(Content)
            .AddContent(Content)
            .Build());
        var budget = new ResourceContentBudget(2L * Content.Length + 100);

        var batch = await HashAsync(path, [0, 1], budget: budget);

        Assert.IsTrue(batch.Results[0].IsSuccess);
        AssertFailure(batch.Results[1], ResourceContentIssueCode.BudgetExhausted);
        Assert.IsTrue(budget.ConsumedBytes <= budget.TotalBytes);
    }

    [TestMethod]
    public async Task IndexKeyThatNoLongerMatchesTheScanIsRejected()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("a.package", new DbpfFixtureBuilder()
            .AddContent(Content, instanceLo: 1)
            .Build());
        var fileSystem = new PhysicalFileSystemAccess();
        var hasher = new DbpfResourceContentHasher(fileSystem);

        var batch = await hasher.HashAsync(
            new ResourceContentBatchRequest(path, fileSystem.GetFileStamp(path),
                [new ResourceContentTarget(new ResourceKey(Type, 0, 2), 0)], ResourceContentLimits.Default),
            new ResourceContentBudget(1 << 20));

        AssertFailure(batch.Results[0], ResourceContentIssueCode.IndexMismatch);
    }

    [TestMethod]
    public async Task FileChangedSinceScanIsNotRead()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("a.package", new DbpfFixtureBuilder().AddContent(Content).Build());
        var fileSystem = new TrackingFileSystem(new PhysicalFileSystemAccess());
        var stamp = fileSystem.GetFileStamp(path);
        var hasher = new DbpfResourceContentHasher(fileSystem);

        var batch = await hasher.HashAsync(
            new ResourceContentBatchRequest(path, stamp with { Length = stamp.Length + 1 },
                Targets(1), ResourceContentLimits.Default),
            new ResourceContentBudget(1 << 20));

        AssertFailure(batch.Results[0], ResourceContentIssueCode.ChangedSinceScan);
        Assert.AreEqual(0, fileSystem.OpenCount);
    }

    [TestMethod]
    public async Task FileChangedDuringReadInvalidatesTheWholeBatch()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("a.package", new DbpfFixtureBuilder().AddContent(Content).AddContent(Content).Build());
        var physical = new PhysicalFileSystemAccess();
        var stamp = physical.GetFileStamp(path);
        var hasher = new DbpfResourceContentHasher(new StampDriftFileSystem(physical, triggerOnCall: 2));

        var batch = await hasher.HashAsync(
            new ResourceContentBatchRequest(path, stamp, Targets(2), ResourceContentLimits.Default),
            new ResourceContentBudget(1 << 20));

        foreach (var result in batch.Results)
        {
            AssertFailure(result, ResourceContentIssueCode.ChangedDuringRead);
        }

        Assert.AreNotEqual(stamp, batch.StampAfterRead);
    }

    [TestMethod]
    public async Task CancellationDuringReadPropagatesAndReleasesTheFile()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("a.package", new DbpfFixtureBuilder()
            .AddContent(DbpfFixtureBuilder.Zlib(Content), DbpfFixtureBuilder.CompressionZlib, (uint)Content.Length)
            .Build());
        using var source = new CancellationTokenSource();
        var fileSystem = new TrackingFileSystem(new PhysicalFileSystemAccess(), cancelAfterReads: 2, source);
        var hasher = new DbpfResourceContentHasher(fileSystem);

        await Assert.ThrowsAsync<OperationCanceledException>(() => hasher.HashAsync(
            new ResourceContentBatchRequest(path, fileSystem.GetFileStamp(path), Targets(1), ResourceContentLimits.Default),
            new ResourceContentBudget(1 << 20),
            source.Token));

        Assert.AreEqual(1, fileSystem.OpenCount);
        Assert.AreEqual(fileSystem.OpenCount, fileSystem.DisposedCount);
    }

    [TestMethod]
    public async Task StreamsAreReleasedOnSuccessAndFailure()
    {
        using var temp = new TempDirectory();
        var good = temp.WriteBytes("good.package", new DbpfFixtureBuilder().AddContent(Content).Build());
        var bad = temp.WriteBytes("bad.package", new DbpfFixtureBuilder()
            .AddContent(Content, DbpfFixtureBuilder.CompressionNone, (uint)Content.Length + 5)
            .Build());
        var fileSystem = new TrackingFileSystem(new PhysicalFileSystemAccess());
        var hasher = new DbpfResourceContentHasher(fileSystem);

        foreach (var path in new[] { good, bad })
        {
            await hasher.HashAsync(
                new ResourceContentBatchRequest(path, fileSystem.GetFileStamp(path), Targets(1), ResourceContentLimits.Default),
                new ResourceContentBudget(1 << 20));
        }

        Assert.AreEqual(2, fileSystem.OpenCount);
        Assert.AreEqual(2, fileSystem.DisposedCount);
    }

    [TestMethod]
    public async Task EndToEndOnlyCandidateResourcesAreReadAndGrouped()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var other = (byte[])Content.Clone();
        other[0] ^= 0xFF;

        // 不是候选的资源故意写成损坏的 zlib：如果被读到，就会出现失败记录。
        var junk = new byte[] { 0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0xFF };
        var a = temp.WriteBytes(Path.Combine("Mods", "a.package"), new DbpfFixtureBuilder()
            .AddContent(Content, instanceLo: 1)
            .AddContent(junk, DbpfFixtureBuilder.CompressionZlib, 100, type: 0x00B2D882, instanceLo: 9)
            .Build());
        var b = temp.WriteBytes(Path.Combine("Mods", "b.package"), new DbpfFixtureBuilder()
            .AddContent(DbpfFixtureBuilder.Zlib(Content), DbpfFixtureBuilder.CompressionZlib, (uint)Content.Length, instanceLo: 1)
            .Build());
        var c = temp.WriteBytes(Path.Combine("Mods", "c.package"), new DbpfFixtureBuilder()
            .AddContent(other, instanceLo: 1)
            .Build());

        var fileSystem = new PhysicalFileSystemAccess();
        var scanner = new PackageConflictScanner(
            fileSystem,
            new LlamaLogicPackageIndexReader(new PackagePrechecker(fileSystem), fileSystem));
        var scan = await scanner.ScanAsync(new PackageConflictScanRequest([new ScanSource("mods", mods)]));
        var comparer = new ResourceContentComparer(new DbpfResourceContentHasher(fileSystem));

        var report = await comparer.CompareAsync(new ResourceContentComparisonRequest(scan.Candidates));

        var comparison = report.Comparisons.Single();
        Assert.AreEqual(ResourceContentVerdict.Different, comparison.Verdict);
        Assert.IsTrue(comparison.IsComplete);
        Assert.AreEqual(0, comparison.Uncompared.Count);
        var groups = comparison.ContentGroups
            .Select(group => group.Occurrences.Select(occurrence => occurrence.PackagePath).ToArray())
            .OrderBy(paths => paths.Length)
            .ToArray();
        CollectionAssert.AreEqual(new[] { c }, groups[0]);
        CollectionAssert.AreEquivalent(new[] { a, b }, groups[1]);
        Assert.IsTrue(report.Packages.All(package => package.IsStable));
    }

    private static async Task<ResourceContentBatchResult> HashAsync(
        string path,
        int[] ordinals,
        ResourceContentLimits? limits = null,
        ResourceContentBudget? budget = null)
    {
        var fileSystem = new PhysicalFileSystemAccess();
        var hasher = new DbpfResourceContentHasher(fileSystem);
        var batch = await hasher.HashAsync(
            new ResourceContentBatchRequest(
                path,
                fileSystem.GetFileStamp(path),
                ordinals.Select(ordinal => new ResourceContentTarget(KeyAt(ordinal), ordinal)).ToArray(),
                limits ?? ResourceContentLimits.Default),
            budget ?? new ResourceContentBudget(1 << 20));
        Assert.AreEqual(ordinals.Length, batch.Results.Count);
        return batch;
    }

    private static ResourceContentTarget[] Targets(int count) =>
        Enumerable.Range(0, count).Select(ordinal => new ResourceContentTarget(KeyAt(ordinal), ordinal)).ToArray();

    /// <summary><see cref="DbpfFixtureBuilder.AddContent"/> 未指定 instance 时的默认键。</summary>
    private static ResourceKey KeyAt(int ordinal) => new(Type, 0, 0xD1D50000u + (uint)ordinal);

    private static void AssertFailure(ResourceContentResult result, string code)
    {
        Assert.IsFalse(result.IsSuccess, $"预期失败 {code}，实际成功。");
        Assert.IsNull(result.Sha256);
        Assert.AreEqual(code, result.Issue!.Code, result.Issue.Detail);
    }

    private static byte[] MakeContent(int length)
    {
        // 有重复也有变化，让 zlib 真正压缩出多个块，而不是一整段常量。
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)((index * 31) ^ (index >> 3));
        }

        return bytes;
    }
}

/// <summary>
/// 记录打开与释放次数；可选在第 N 次读取时触发取消，用来验证中途取消也会释放句柄。
/// </summary>
internal sealed class TrackingFileSystem(
    IFileSystemAccess inner,
    int cancelAfterReads = 0,
    CancellationTokenSource? source = null) : IFileSystemAccess
{
    private int _reads;

    public int OpenCount { get; private set; }

    public int DisposedCount { get; private set; }

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public IReadOnlyList<string> EnumerateFileSystemEntries(string directory) =>
        inner.EnumerateFileSystemEntries(directory);

    public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);

    public FileStamp GetFileStamp(string path) => inner.GetFileStamp(path);

    public Stream OpenRead(string path)
    {
        OpenCount++;
        return new TrackingStream(inner.OpenRead(path), this);
    }

    private void OnRead()
    {
        if (cancelAfterReads > 0 && ++_reads >= cancelAfterReads)
        {
            source?.Cancel();
        }
    }

    private sealed class TrackingStream(Stream inner, TrackingFileSystem owner) : Stream
    {
        private bool _disposed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            owner.OnRead();
            return inner.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            owner.OnRead();
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                owner.DisposedCount++;
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner.DisposedCount++;
                await inner.DisposeAsync().ConfigureAwait(false);
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
