using System.Buffers.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Packages;

namespace Sims4ModDoctor.Core.Tests.Packages;

[TestClass]
public sealed class DbpfPrecheckTests
{
    private static PackageReadIssue? Inspect(
        byte[] package,
        out PackagePrecheckResult? result,
        PackageSafetyLimits? limits = null,
        string fileName = "sample.package")
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes(fileName, package);
        return PackagePrechecker.CreateDefault()
            .Inspect(path, limits ?? PackageSafetyLimits.Default, out result);
    }

    [TestMethod]
    public void AcceptsAMinimalPackageAndReportsHeaderFields()
    {
        var package = new DbpfFixtureBuilder().AddResources(3).Build();

        var issue = Inspect(package, out var result);

        Assert.IsNull(issue);
        Assert.IsNotNull(result);
        Assert.AreEqual(2u, result.Header.Major);
        Assert.AreEqual(1u, result.Header.Minor);
        Assert.AreEqual(3u, result.Header.EntryCount);
        Assert.AreEqual(3u, result.Header.IndexVersion);
        Assert.AreEqual(package.Length, result.StampBefore.Length);
    }

    [TestMethod]
    public void AcceptsAnEmptyPackage()
    {
        var package = new DbpfFixtureBuilder().Build();

        var issue = Inspect(package, out var result);

        Assert.IsNull(issue);
        Assert.IsNotNull(result);
        Assert.AreEqual(0u, result.Header.EntryCount);
    }

    // 位域决定每条记录在磁盘上占几个 DWORD。第三方库自带的样本位域为 0，
    // 这几个用例是它覆盖不到的部分。
    [TestMethod]
    [DataRow(0x00u, DisplayName = "没有公共常量")]
    [DataRow(0x01u, DisplayName = "Type 提为公共常量")]
    [DataRow(0x03u, DisplayName = "Type 与 Group 都提为公共常量")]
    [DataRow(0x0Fu, DisplayName = "整个 TGI 都提为公共常量")]
    [DataRow(0xFFu, DisplayName = "八个字段全部提为公共常量")]
    public void AcceptsEveryKnownConstantFieldLayout(uint indexType)
    {
        var package = new DbpfFixtureBuilder()
            .WithIndexType(indexType)
            .AddResources(5)
            .Build();

        var issue = Inspect(package, out var result);

        Assert.IsNull(issue);
        Assert.IsNotNull(result);
        Assert.AreEqual(5u, result.Header.EntryCount);
    }

    [TestMethod]
    public void RejectsAnIndexTypeWithBitsOutsideTheKnownEight()
    {
        var package = new DbpfFixtureBuilder().AddResources(2).Build();
        var indexPosition = BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(0x40));
        BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan((int)indexPosition), 0x100);

        var issue = Inspect(package, out var result);

        Assert.IsNotNull(issue);
        Assert.AreEqual(PackageReadIssueCode.UnsupportedIndexVersion, issue.Code);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void RejectsAFileShorterThanTheHeader()
    {
        var package = new DbpfFixtureBuilder().AddResource().Build();

        var issue = Inspect(DbpfFixtureBuilder.Truncate(package, 40), out var result);

        Assert.IsNotNull(issue);
        Assert.AreEqual(PackageReadIssueCode.TooSmallForHeader, issue.Code);
        Assert.AreEqual(PackageReadStage.Precheck, issue.Stage);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void RejectsAFileThatIsNotAPackage()
    {
        var package = new DbpfFixtureBuilder().WithMagic("ZIP\0").AddResource().Build();

        var issue = Inspect(package, out _);

        Assert.IsNotNull(issue);
        Assert.AreEqual(PackageReadIssueCode.MagicMismatch, issue.Code);
    }

    // 次版本必须精确匹配：Sims 2 的包是 1.1，社区文档里还流传着 2.0 的写法。
    [TestMethod]
    [DataRow(1u, 1u, DisplayName = "Sims 2 的 DBPF 1.1")]
    [DataRow(2u, 0u, DisplayName = "社区文档里的 2.0")]
    [DataRow(3u, 1u, DisplayName = "未来的主版本")]
    public void RejectsUnsupportedFileVersions(uint major, uint minor)
    {
        var package = new DbpfFixtureBuilder()
            .WithVersion(major, minor)
            .AddResource()
            .Build();

        var issue = Inspect(package, out _);

        Assert.IsNotNull(issue);
        Assert.AreEqual(PackageReadIssueCode.UnsupportedVersion, issue.Code);
    }

    [TestMethod]
    public void RejectsAnUnsupportedIndexVersion()
    {
        var package = new DbpfFixtureBuilder()
            .WithIndexVersion(2)
            .AddResource()
            .Build();

        var issue = Inspect(package, out _);

        Assert.IsNotNull(issue);
        Assert.AreEqual(PackageReadIssueCode.UnsupportedIndexVersion, issue.Code);
    }

    [TestMethod]
    public void RejectsAnAbsurdResourceCountBeforeAnythingIsAllocated()
    {
        var package = new DbpfFixtureBuilder().AddResource().Build();
        BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(0x24), uint.MaxValue);

        var issue = Inspect(package, out var result);

        Assert.IsNotNull(issue);
        Assert.AreEqual(PackageReadIssueCode.ResourceCountExceedsLimit, issue.Code);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void RejectsAnIndexThatStartsPastTheEndOfTheFile()
    {
        var package = new DbpfFixtureBuilder().AddResources(2).Build();
        BinaryPrimitives.WriteUInt32LittleEndian(
            package.AsSpan(0x40),
            (uint)(package.Length + 1024));

        var issue = Inspect(package, out _);

        Assert.IsNotNull(issue);
        Assert.AreEqual(PackageReadIssueCode.IndexOutOfBounds, issue.Code);
    }

    // position 与 size 都是 uint32；用 32 位算术相加会回绕成一个很小的合法值。
    [TestMethod]
    public void RejectsAnIndexRangeThatOnlyFitsWhenTheAdditionOverflows()
    {
        var package = new DbpfFixtureBuilder().AddResources(2).Build();
        BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(0x40), 0xFFFFFF00);
        BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(0x2C), 0x00000200);

        var issue = Inspect(package, out _);

        Assert.IsNotNull(issue);
        Assert.AreEqual(PackageReadIssueCode.IndexOutOfBounds, issue.Code);
    }

    [TestMethod]
    public void RejectsAnEmptyIndexThatStillClaimsASizeAndPosition()
    {
        var package = new DbpfFixtureBuilder().AddResources(2).Build();
        BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(0x24), 0);

        var issue = Inspect(package, out _);

        Assert.IsNotNull(issue);
        Assert.AreEqual(PackageReadIssueCode.IndexOutOfBounds, issue.Code);
    }

    [TestMethod]
    public void RejectsATruncatedIndex()
    {
        var package = new DbpfFixtureBuilder().AddResources(6).Build();

        var issue = Inspect(DbpfFixtureBuilder.Truncate(package, package.Length - 20), out _);

        Assert.IsNotNull(issue);
        Assert.AreEqual(PackageReadIssueCode.IndexOutOfBounds, issue.Code);
    }

    // 声明的索引长度必须和「资源数 × 每条实际字段数」对得上。
    // 少算索引头里的公共常量时，这个用例在位域为 0 的样本上依然会通过，
    // 因此必须用一个有置位的位域来验。
    [TestMethod]
    public void RejectsAnIndexSizeThatDisagreesWithTheConstantFieldLayout()
    {
        var package = new DbpfFixtureBuilder()
            .WithIndexType(0x03)
            .AddResources(4)
            .Build();
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(0x2C));
        BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(0x2C), declared - 8);

        var issue = Inspect(package, out _);

        Assert.IsNotNull(issue);
        Assert.AreEqual(PackageReadIssueCode.IndexSizeMismatch, issue.Code);
    }

    [TestMethod]
    public void ReportsAMissingFileWithoutThrowing()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "不存在的.package");

        var issue = PackagePrechecker.CreateDefault()
            .Inspect(missing, PackageSafetyLimits.Default, out var result);

        Assert.IsNotNull(issue);
        Assert.AreEqual(PackageReadIssueCode.NotFound, issue.Code);
        Assert.AreEqual(PackageReadStage.Access, issue.Stage);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void SupportsLongUnicodePaths()
    {
        using var temp = new TempDirectory();
        var parts = Enumerable.Range(0, 10)
            .Select(index => $"很长的目录_{index:D2}_abcdefghijkl")
            .ToArray();
        var directory = temp.CreateDirectory(parts);
        var relative = Path.Combine(Path.GetRelativePath(temp.Path, directory), "模拟人生.package");
        var path = temp.WriteBytes(relative, new DbpfFixtureBuilder().AddResources(2).Build());
        Assert.IsGreaterThan(260, directory.Length);

        var issue = PackagePrechecker.CreateDefault()
            .Inspect(path, PackageSafetyLimits.Default, out var result);

        Assert.IsNull(issue);
        Assert.IsNotNull(result);
        Assert.AreEqual(2u, result.Header.EntryCount);
    }

    [TestMethod]
    public void RejectsAPackageLargerThanTheConfiguredLimit()
    {
        var package = new DbpfFixtureBuilder().AddResources(2).Build();

        var issue = Inspect(
            package,
            out _,
            new PackageSafetyLimits(MaxFileSizeBytes: 32));

        Assert.IsNotNull(issue);
        Assert.AreEqual(PackageReadIssueCode.TooLarge, issue.Code);
    }
}
