using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Reporting;

namespace Sims4ModSieve.Core.Tests;

[TestClass]
public sealed class ReportRenderingTests
{
    [TestMethod]
    public void JsonIsCamelCaseAndHtmlEscapesPathsAndMessages()
    {
        var file = new DuplicateFile(
            @"D:\Mods\A&B.package",
            4,
            DateTime.UnixEpoch,
            ["mods"],
            ["mods"],
            true,
            3);
        var second = file with { Path = @"D:\Mods\B.package" };
        var group = new DuplicateGroup(
            new string('A', 64),
            4,
            DuplicateSection.Ordinary,
            [file, second],
            file.Path,
            [second.Path]);
        var report = new DuplicateReport(
            DateTime.UnixEpoch,
            DateTime.UnixEpoch,
            [new ScanSourceSnapshot("mods", @"D:\Mods", 0, "Mods", false, false)],
            2,
            2,
            [group],
            [new ScanIssue("test", ScanIssueStage.Hashing, @"D:\<bad>", "A&B")]);

        var json = DuplicateReportJson.Serialize(report);
        var html = DuplicateHtmlReportRenderer.Render(report);

        StringAssert.Contains(json, "\"discoveredFileCount\": 2");
        StringAssert.Contains(json, "\"section\": \"ordinary\"");
        StringAssert.Contains(json, "Mods");
        StringAssert.Contains(html, "A&amp;B.package");
        StringAssert.Contains(html, "<title>Sims 4 Mod Sieve｜重复文件报告</title>");
        StringAssert.Contains(html, "<h1>Sims 4 Mod Sieve｜重复文件报告</h1>");
        StringAssert.Contains(html, "D:\\&lt;bad&gt;");
        Assert.DoesNotContain("D:\\<bad>", html, StringComparison.Ordinal);
    }
}
