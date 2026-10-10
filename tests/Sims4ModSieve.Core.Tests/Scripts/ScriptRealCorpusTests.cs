using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;

namespace Sims4ModSieve.Core.Tests.Scripts;

/// <summary>
/// 针对本机真实脚本 mod 的验证。默认跳过，不随仓库分发任何真实 Mod。
/// </summary>
/// <remarks>
/// 运行方式：
/// <code>
/// SIMS4_MOD_SIEVE_REAL_MODS=/path/to/Mods dotnet test tests/Sims4ModSieve.Core.Tests --filter TestCategory=RealCorpus
/// </code>
/// </remarks>
[TestClass]
public sealed class ScriptRealCorpusTests
{
    private const string DirectoryVariable = "SIMS4_MOD_SIEVE_REAL_MODS";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("RealCorpus")]
    public async Task ReadsEveryScriptInTheLocalCorpus()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            Assert.Inconclusive($"Set {DirectoryVariable} to a Mods directory to run this test.");
        }

        var report = await new ScriptModuleScanner(new PhysicalFileSystemAccess())
            .ScanAsync(new ScriptModuleScanRequest([new ScanSource("mods", directory)]));

        TestContext.WriteLine(
            $"archives {report.DiscoveredArchiveCount}, analyzed {report.AnalyzedArchiveCount}, " +
            $"modules {report.ModuleCount}, collisions {report.Collisions.Count}, processed {report.ProcessedBytes} bytes");
        foreach (var collision in report.Collisions)
        {
            TestContext.WriteLine($"  {collision.ModuleName}: {collision.Verdict}, {collision.ArchiveCount} archives");
        }
        foreach (var issue in report.Issues)
        {
            TestContext.WriteLine($"  issue {issue.Code} {issue.Path} {issue.EntryName} {issue.Detail}");
        }

        // 玩家正常在用的脚本不应该被拦下来。
        Assert.AreEqual(0, report.IncompleteArchiveCount);
        Assert.AreEqual(0, report.Issues.Count);
        Assert.AreEqual(report.DiscoveredArchiveCount, report.AnalyzedArchiveCount);
    }
}
