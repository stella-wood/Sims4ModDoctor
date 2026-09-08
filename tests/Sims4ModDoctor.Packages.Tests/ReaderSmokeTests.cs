using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Tests;
using Sims4ModDoctor.Core.Tests.Packages;

namespace Sims4ModDoctor.Packages.Tests;

/// <summary>
/// 先确认自造 fixture 确实是第三方库认得的 DBPF：
/// 如果这条不成立，后面所有基于 fixture 的断言都失去意义。
/// </summary>
[TestClass]
public sealed class ReaderSmokeTests
{
    [TestMethod]
    public async Task ReadsAFixtureBuiltByThisRepository()
    {
        using var temp = new TempDirectory();
        var bytes = new DbpfFixtureBuilder()
            .AddResource(type: 0x0904DF10, group: 0x0000000A, instanceLo: 0x11112222, compressed: 0)
            .AddResource(type: 0x545AC67A, group: 0x0000000A, instanceLo: 0x33334444, compressed: 0)
            .Build();
        var path = temp.WriteBytes("fixture.package", bytes);

        var result = await LlamaLogicPackageIndexReader.CreateDefault().ReadIndexAsync(path);

        Assert.IsTrue(result.IsSuccess, $"{result.Issue?.Code} {result.Issue?.Detail}");
        Assert.IsNotNull(result.Summary);
        Assert.AreEqual(2, result.Summary.DbpfMajorVersion);
        Assert.AreEqual(1, result.Summary.DbpfMinorVersion);
        Assert.AreEqual(2, result.Summary.ResourceCount);
    }
}
