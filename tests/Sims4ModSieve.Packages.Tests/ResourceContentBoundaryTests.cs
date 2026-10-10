using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Core.Tests.Packages;

namespace Sims4ModSieve.Packages.Tests;

[TestClass]
public sealed class ResourceContentBoundaryTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(65536)]
    [DataRow(65537)]
    [DataRow(200000)]
    public async Task CompleteZlibUsesExactlyItsInputAndOutputBudget(int length)
    {
        var content = new byte[length];
        new Random(17).NextBytes(content);
        var compressed = DbpfFixtureBuilder.Zlib(content);
        var budget = new ResourceContentBudget((long)compressed.Length + length);
        var result = await Read(compressed, DbpfFixtureBuilder.CompressionZlib, (uint)length, budget);
        Assert.IsTrue(result.IsSuccess, result.Issue?.Code + ": " + result.Issue?.Detail);
        Assert.AreEqual(budget.TotalBytes, budget.ConsumedBytes);
        Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)), result.Sha256);
    }

    [TestMethod]
    public async Task CompressedShortReadsUseActualBytes()
    {
        byte[] content = [1, 2, 3, 4, 5];
        var compressed = DbpfFixtureBuilder.Zlib(content);
        var budget = new ResourceContentBudget(compressed.Length + content.Length);
        var result = await Read(compressed, DbpfFixtureBuilder.CompressionZlib, 5, budget, shortReads: true);
        Assert.IsTrue(result.IsSuccess, result.Issue?.Code);
        Assert.AreEqual(budget.TotalBytes, budget.ConsumedBytes);
    }

    [TestMethod]
    public async Task OutputBudgetCannotBeExceededByOneChunk()
    {
        var compressed = DbpfFixtureBuilder.Zlib(new byte[10000]);
        var budget = new ResourceContentBudget(compressed.Length + 7);
        var result = await Read(compressed, DbpfFixtureBuilder.CompressionZlib, 10000, budget);
        Assert.AreEqual(ResourceContentIssueCode.BudgetExhausted, result.Issue?.Code);
        Assert.IsNull(result.Sha256);
        Assert.AreEqual(budget.TotalBytes, budget.ConsumedBytes);
    }

    [TestMethod]
    public async Task TrailingBytesAndConcatenatedStreamsAreRejected()
    {
        var compressed = DbpfFixtureBuilder.Zlib([0x41]);
        foreach (var stored in new byte[][]
        {
            [.. compressed, 0, .. compressed[^4..]],
            [.. compressed, .. compressed],
            compressed[..^1],
        })
        {
            var result = await Read(stored, DbpfFixtureBuilder.CompressionZlib, 1, new ResourceContentBudget(1000));
            Assert.IsFalse(result.IsSuccess);
            Assert.IsNull(result.Sha256);
        }
    }

    [TestMethod]
    public async Task NonFinalDeflateBlockMustNotBeAcceptedAsComplete()
    {
        // zlib header, non-final stored block containing A, then A's Adler32.
        // There is deliberately no final DEFLATE block.
        byte[] malformed = [0x78, 0x01, 0x00, 0x01, 0x00, 0xfe, 0xff, 0x41, 0x00, 0x42, 0x00, 0x42];
        var result = await Read(malformed, DbpfFixtureBuilder.CompressionZlib, 1, new ResourceContentBudget(100));
        Assert.IsFalse(result.IsSuccess, $"Incomplete zlib accepted: {result.Sha256}");
    }

    [TestMethod]
    public async Task UncompressedContentMustChargeBothInputAndOutput()
    {
        var budget = new ResourceContentBudget(100);
        var result = await Read(new byte[8], 0, 8, budget);
        Assert.IsTrue(result.IsSuccess, result.Issue?.Code);
        Assert.AreEqual(16L, budget.ConsumedBytes, "Budget contract counts stored input plus output.");
    }

    [TestMethod]
    public async Task RejectedZlibOutputMustStillBeAccountedFor()
    {
        var compressed = DbpfFixtureBuilder.Zlib(new byte[10000]);
        var budget = new ResourceContentBudget(1000);
        var result = await Read(compressed, DbpfFixtureBuilder.CompressionZlib, 1, budget);
        Assert.AreEqual(ResourceContentIssueCode.LengthMismatch, result.Issue?.Code);
        // With cap=1 the inflater requests and produces 2 bytes before rejecting.
        Assert.AreEqual((long)compressed.Length + 2, budget.ConsumedBytes);
    }

    [TestMethod]
    public async Task ShortReadsMustNotExhaustAnOtherwiseSufficientBudget()
    {
        var result = await Read(new byte[8], 0, 8, new ResourceContentBudget(16), shortReads: true);
        Assert.IsTrue(result.IsSuccess, result.Issue?.Code);
    }

    private static async Task<ResourceContentResult> Read(byte[] payload, ushort compression, uint size,
        ResourceContentBudget budget, bool shortReads = false)
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("test.package", new DbpfFixtureBuilder().AddContent(payload, compression, size).Build());
        IFileSystemAccess fs = new PhysicalFileSystemAccess();
        if (shortReads) fs = new ShortReadFileSystem(fs);
        var result = await new DbpfResourceContentHasher(fs).HashAsync(new ResourceContentBatchRequest(
            path, fs.GetFileStamp(path), [new(new ResourceKey(0x0904DF10, 0, 0xD1D50000), 0)], ResourceContentLimits.Default), budget);
        return result.Results.Single();
    }

    private sealed class ShortReadFileSystem(IFileSystemAccess inner) : IFileSystemAccess
    {
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public IReadOnlyList<string> EnumerateFileSystemEntries(string path) => inner.EnumerateFileSystemEntries(path);
        public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);
        public FileStamp GetFileStamp(string path) => inner.GetFileStamp(path);
        public Stream OpenRead(string path) => new ShortStream(inner.OpenRead(path));
    }

    private sealed class ShortStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] b, int o, int c) => inner.Read(b, o, Math.Min(c, 1));
        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken t = default) => inner.ReadAsync(b[..Math.Min(b.Length, 1)], t);
        public override long Seek(long o, SeekOrigin s) => inner.Seek(o, s);
        public override void Flush() { }
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
