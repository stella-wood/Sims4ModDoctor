using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Core.Packages;
using Sims4ModDoctor.Core.Tests;
using Sims4ModDoctor.Core.Tests.Packages;

namespace Sims4ModDoctor.Packages.Tests;

[TestClass]
public sealed class LlamaLogicPackageIndexReaderTests
{
    private static LlamaLogicPackageIndexReader CreateReader(IFileSystemAccess fileSystem) =>
        new(new PackagePrechecker(fileSystem), fileSystem);

    [TestMethod]
    public async Task ReportsEveryResourceKeyInIndexOrder()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("角色.package", new DbpfFixtureBuilder()
            .AddResource(type: 0x034AEECB, group: 0x0000000A, instanceLo: 0xAAAA0001, compressed: 0)
            .AddResource(type: 0x034AEECB, group: 0x0000000A, instanceLo: 0xAAAA0002, compressed: 0)
            .AddResource(type: 0x00B2D882, group: 0x00000000, instanceLo: 0xBBBB0001, compressed: 0)
            .Build());

        var result = await LlamaLogicPackageIndexReader.CreateDefault().ReadIndexAsync(path);

        Assert.IsTrue(result.IsSuccess, $"{result.Issue?.Code} {result.Issue?.Detail}");
        Assert.IsNotNull(result.Summary);
        Assert.AreEqual(3, result.Summary.ResourceCount);
        CollectionAssert.AreEqual(
            new[] { 0, 1, 2 },
            result.Summary.Resources.Select(entry => entry.Ordinal).ToArray());
        CollectionAssert.AreEqual(
            new[] { 0x034AEECBu, 0x034AEECBu, 0x00B2D882u },
            result.Summary.Resources.Select(entry => entry.Key.Type).ToArray());
    }

    [TestMethod]
    public async Task KeepsTheRawCompressionValueEvenWhenItCannotBeClassified()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("资源.package", new DbpfFixtureBuilder()
            .AddResource(compressed: 0)
            .Build());

        var result = await LlamaLogicPackageIndexReader.CreateDefault().ReadIndexAsync(path);

        Assert.IsNotNull(result.Summary);
        var entry = result.Summary.Resources.Single();
        Assert.IsFalse(string.IsNullOrWhiteSpace(entry.CompressionRaw));
    }

    // 需求：OperationCanceledException 必须原样传播，不得被转成失败结果。
    [TestMethod]
    public async Task PropagatesCancellationInsteadOfReturningAFailure()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("取消.package", new DbpfFixtureBuilder().AddResources(4).Build());
        using var source = new CancellationTokenSource();
        var reader = CreateReader(
            new CancelOnOpenFileSystem(new PhysicalFileSystemAccess(), source));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => reader.ReadIndexAsync(path, source.Token));
    }

    [TestMethod]
    public async Task PropagatesCancellationRequestedBeforeTheCallStarts()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("先取消.package", new DbpfFixtureBuilder().AddResource().Build());
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => LlamaLogicPackageIndexReader.CreateDefault().ReadIndexAsync(path, source.Token));
    }

    // 需求：读取前后文件发生变化时不得返回完整成功。
    [TestMethod]
    public async Task RefusesToReportSuccessWhenTheFileChangesWhileItIsBeingRead()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("变动.package", new DbpfFixtureBuilder().AddResources(3).Build());

        // 第一次取戳发生在预检，第二次发生在读取之后；只让第二次漂移。
        var reader = CreateReader(new StampDriftFileSystem(new PhysicalFileSystemAccess(), 2));
        var result = await reader.ReadIndexAsync(path);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNotNull(result.Issue);
        Assert.AreEqual(PackageReadIssueCode.ChangedDuringRead, result.Issue.Code);
        Assert.AreEqual(PackageReadStage.Stability, result.Issue.Stage);
        Assert.IsNull(result.Summary);
    }

    // 需求：单个损坏文件不能导致批量分析整体失败。
    [TestMethod]
    public async Task LetsOneBrokenFileFailWithoutAffectingTheRestOfTheBatch()
    {
        using var temp = new TempDirectory();
        var good = new DbpfFixtureBuilder().AddResources(2).Build();
        var paths = new[]
        {
            temp.WriteBytes("一号.package", good),
            temp.WriteBytes("二号.package", DbpfFixtureBuilder.Truncate(good, good.Length - 12)),
            temp.WriteBytes("三号.package", new DbpfFixtureBuilder().WithMagic("ZIP\0").AddResource().Build()),
            temp.WriteBytes("四号.package", good),
        };

        var reader = LlamaLogicPackageIndexReader.CreateDefault();
        var results = new List<PackageReadResult>();
        foreach (var path in paths)
        {
            results.Add(await reader.ReadIndexAsync(path));
        }

        Assert.IsTrue(results[0].IsSuccess);
        Assert.IsFalse(results[1].IsSuccess);
        Assert.IsFalse(results[2].IsSuccess);
        Assert.IsTrue(results[3].IsSuccess, "一个坏文件不应影响它后面的文件。");
        Assert.AreEqual(PackageReadIssueCode.MagicMismatch, results[2].Issue?.Code);
    }

    [TestMethod]
    public async Task ReturnsAStructuredFailureForAMissingFile()
    {
        using var temp = new TempDirectory();

        var result = await LlamaLogicPackageIndexReader.CreateDefault()
            .ReadIndexAsync(Path.Combine(temp.Path, "没有这个文件.package"));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PackageReadIssueCode.NotFound, result.Issue?.Code);
    }

    [TestMethod]
    public async Task ReadsThroughLongUnicodePaths()
    {
        using var temp = new TempDirectory();
        var parts = Enumerable.Range(0, 10)
            .Select(index => $"很长的目录_{index:D2}_abcdefghijkl")
            .ToArray();
        var directory = temp.CreateDirectory(parts);
        var relative = Path.Combine(
            Path.GetRelativePath(temp.Path, directory),
            "模拟人生角色.package");
        var path = temp.WriteBytes(relative, new DbpfFixtureBuilder().AddResources(2).Build());
        Assert.IsGreaterThan(260, directory.Length);

        var result = await LlamaLogicPackageIndexReader.CreateDefault().ReadIndexAsync(path);

        Assert.IsTrue(result.IsSuccess, $"{result.Issue?.Code} {result.Issue?.Detail}");
        Assert.AreEqual(2, result.Summary?.ResourceCount);
    }

    [TestMethod]
    public async Task StopsBeforeTheLibrarySeesAnAbsurdResourceCount()
    {
        using var temp = new TempDirectory();
        var package = new DbpfFixtureBuilder().AddResource().Build();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            package.AsSpan(0x24),
            uint.MaxValue);
        var path = temp.WriteBytes("恶意数量.package", package);

        var result = await LlamaLogicPackageIndexReader.CreateDefault().ReadIndexAsync(path);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PackageReadIssueCode.ResourceCountExceedsLimit, result.Issue?.Code);
        Assert.AreEqual(PackageReadStage.Precheck, result.Issue?.Stage);
    }
}
