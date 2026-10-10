using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Core.Tests.Packages;

namespace Sims4ModSieve.Packages.Tests;

/// <summary>
/// 候选冲突扫描接上真实读取器，跑自造的 DBPF fixture。不使用任何真实玩家 Mod。
/// </summary>
[TestClass]
public sealed class ConflictScanIntegrationTests
{
    [TestMethod]
    public async Task TwoFixturePackagesSharingATgiProduceACandidate()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var hairA = temp.WriteBytes(Path.Combine("Mods", "发型A.package"), new DbpfFixtureBuilder()
            .AddResource(type: 0x034AEECB, group: 0x0000000A, instanceLo: 0xAAAA0001)
            .AddResource(type: 0x00B2D882, group: 0x00000000, instanceLo: 0xBBBB0001)
            .Build());
        var hairB = temp.WriteBytes(Path.Combine("Mods", "发型B.package"), new DbpfFixtureBuilder()
            .AddResource(type: 0x00B2D882, group: 0x00000000, instanceLo: 0xBBBB0002)
            .AddResource(type: 0x034AEECB, group: 0x0000000A, instanceLo: 0xAAAA0001)
            .Build());
        temp.WriteBytes(Path.Combine("Mods", "无关.package"), new DbpfFixtureBuilder()
            .AddResource(type: 0x0333406C, group: 0x00000000, instanceLo: 0xCCCC0001)
            .Build());
        var broken = temp.WriteBytes(Path.Combine("Mods", "损坏.package"), new DbpfFixtureBuilder()
            .WithMagic("NOPE")
            .AddResource()
            .Build());

        var fileSystem = new PhysicalFileSystemAccess();
        var scanner = new PackageConflictScanner(
            fileSystem,
            new LlamaLogicPackageIndexReader(new PackagePrechecker(fileSystem), fileSystem));

        var report = await scanner.ScanAsync(
            new PackageConflictScanRequest([new ScanSource("mods", mods)]));

        Assert.AreEqual(4, report.DiscoveredPackageCount);
        Assert.AreEqual(3, report.AnalyzedPackageCount);
        CollectionAssert.AreEqual(new[] { broken }, report.IncompletePackagePaths.ToArray());
        Assert.AreEqual(PackageReadIssueCode.MagicMismatch, report.Issues.Single().Code);

        var candidate = report.Candidates.Single();
        Assert.AreEqual("034AEECB:0000000A:00000000AAAA0001", candidate.Key.Tgi);
        CollectionAssert.AreEqual(
            new[] { hairA, hairB },
            candidate.Occurrences.Select(occurrence => occurrence.PackagePath).ToArray());
        CollectionAssert.AreEqual(
            new[] { 0, 1 },
            candidate.Occurrences.Select(occurrence => occurrence.Ordinal).ToArray());
    }
}
