using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Core.Scripts;

namespace Sims4ModDoctor.Core.Tests.Scripts;

[TestClass]
public sealed class ScriptModuleScannerTests
{
    /// <summary>Python 3.7 的 .pyc magic（3394），结尾是 \r\n。</summary>
    private static readonly byte[] Py37Magic = [0x42, 0x0D, 0x0D, 0x0A];

    private static ScriptModuleScanner CreateScanner() => new(new PhysicalFileSystemAccess());

    private static ScriptModuleScanRequest Request(string mods, ScriptModuleLimits? limits = null) =>
        new([new ScanSource("mods", mods, Label: "Mods")], limits);

    private static byte[] Pyc(string body, uint sourceMtime = 0x5F000000)
    {
        var header = new byte[16];
        Py37Magic.CopyTo(header, 0);
        BitConverter.GetBytes(sourceMtime).CopyTo(header, 8);
        return [.. header, .. System.Text.Encoding.UTF8.GetBytes(body)];
    }

    private static string WriteScript(TempDirectory temp, string relativePath, params (string Name, byte[] Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }
        return temp.WriteBytes(relativePath, memory.ToArray());
    }

    [TestMethod]
    public async Task SameModuleWithDifferentBytecodeIsDifferent()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var first = WriteScript(temp, "Mods/a.ts4script", ("sharedlib/util.pyc", Pyc("version one")));
        var second = WriteScript(temp, "Mods/b.ts4script", ("sharedlib/util.pyc", Pyc("version two")));

        var report = await CreateScanner().ScanAsync(Request(mods));

        Assert.AreEqual(2, report.DiscoveredArchiveCount);
        Assert.AreEqual(2, report.AnalyzedArchiveCount);
        Assert.AreEqual(1, report.Collisions.Count);
        var collision = report.Collisions[0];
        Assert.AreEqual("sharedlib.util", collision.ModuleName);
        Assert.AreEqual("sharedlib", collision.TopLevelName);
        Assert.AreEqual(2, collision.ArchiveCount);
        Assert.AreEqual(ScriptModuleVerdict.Different, collision.Verdict);
        CollectionAssert.AreEquivalent(
            new[] { first, second },
            collision.Occurrences.Select(occurrence => occurrence.ArchivePath).ToArray());
    }

    [TestMethod]
    public async Task SameBytecodeCompiledAtDifferentTimesIsIdentical()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        WriteScript(temp, "Mods/a.ts4script", ("sharedlib/__init__.pyc", Pyc("same code", sourceMtime: 1)));
        WriteScript(temp, "Mods/sub/b.ts4script", ("sharedlib/__init__.pyc", Pyc("same code", sourceMtime: 2)));

        var report = await CreateScanner().ScanAsync(Request(mods));

        var collision = report.Collisions.Single();
        Assert.AreEqual("sharedlib", collision.ModuleName);
        Assert.AreEqual(ScriptModuleVerdict.Identical, collision.Verdict);
        Assert.AreEqual(ScriptModuleKind.Package, collision.ContentGroups[0].Occurrences[0].Kind);
        Assert.AreEqual(2, collision.ContentGroups[0].Occurrences.Count);
    }

    [TestMethod]
    public async Task HeaderIsHashedWhenItDoesNotLookLikePyc()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        // 16 字节以上、但第 3、4 字节不是 \r\n：不像 .pyc 头部，整份参与哈希。
        WriteScript(temp, "Mods/a.ts4script", ("m.pyc", "AAAAAAAAAAAAAAAA-same"u8.ToArray()));
        WriteScript(temp, "Mods/b.ts4script", ("m.pyc", "BBBBBBBBBBBBBBBB-same"u8.ToArray()));

        var report = await CreateScanner().ScanAsync(Request(mods));

        Assert.AreEqual(ScriptModuleVerdict.Different, report.Collisions.Single().Verdict);
    }

    [TestMethod]
    public async Task NonModuleEntriesAndPycacheAreIgnored()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        // 真实样本：MCCC 每个脚本里都带一份 _DO_NOT_UNZIP_.txt，不是模块，不能报成碰撞。
        WriteScript(temp, "Mods/mc_cas.ts4script",
            ("_DO_NOT_UNZIP_.txt", "hello"u8.ToArray()),
            ("mc_cas.pyc", Pyc("cas")),
            ("__pycache__/shared.cpython-37.pyc", Pyc("cache a")));
        WriteScript(temp, "Mods/mc_cheats.ts4script",
            ("_DO_NOT_UNZIP_.txt", "hello"u8.ToArray()),
            ("mc_cheats.pyc", Pyc("cheats")),
            ("__pycache__/shared.cpython-37.pyc", Pyc("cache b")),
            ("folder/", []),
            ("readme.md", "x"u8.ToArray()));

        var report = await CreateScanner().ScanAsync(Request(mods));

        Assert.AreEqual(0, report.Collisions.Count);
        Assert.AreEqual(2, report.ModuleCount);
        Assert.AreEqual(0, report.Issues.Count);
    }

    [TestMethod]
    public async Task OnlyTheEntryZipimportWouldLoadIsCompared()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        // 同一归档里 lib/__init__.pyc、lib/__init__.py、lib.pyc 都叫 lib：zipimport 先找到包的 .pyc。
        WriteScript(temp, "Mods/a.ts4script",
            ("lib.pyc", Pyc("module form")),
            ("lib/__init__.py", "source form"u8.ToArray()),
            ("lib/__init__.pyc", Pyc("package form")));
        WriteScript(temp, "Mods/b.ts4script", ("lib/__init__.pyc", Pyc("package form")));

        var report = await CreateScanner().ScanAsync(Request(mods));

        var collision = report.Collisions.Single();
        Assert.AreEqual(ScriptModuleVerdict.Identical, collision.Verdict);
        Assert.IsTrue(collision.Occurrences.All(occurrence => occurrence.EntryName == "lib/__init__.pyc"));
        Assert.AreEqual(1, report.ModuleCount);
    }

    [TestMethod]
    public async Task ModuleInsideOneArchiveIsNotACollision()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var inner = temp.CreateDirectory("Mods", "Inner");
        WriteScript(temp, "Mods/Inner/a.ts4script", ("x.pyc", Pyc("x")));

        // 父子目录同时配置：同一个物理文件只处理一次，不会自己和自己撞。
        var report = await CreateScanner().ScanAsync(new ScriptModuleScanRequest(
        [
            new ScanSource("mods", mods, Order: 0),
            new ScanSource("inner", inner, Order: 1),
        ]));

        Assert.AreEqual(1, report.DiscoveredArchiveCount);
        Assert.AreEqual(0, report.Collisions.Count);
    }

    [TestMethod]
    public async Task NotAZipIsReportedAndLeftOut()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var broken = temp.WriteFile("Mods/renamed.ts4script", "this is not a zip at all");
        WriteScript(temp, "Mods/ok.ts4script", ("ok.pyc", Pyc("ok")));

        var report = await CreateScanner().ScanAsync(Request(mods));

        Assert.AreEqual(2, report.DiscoveredArchiveCount);
        Assert.AreEqual(1, report.AnalyzedArchiveCount);
        CollectionAssert.AreEqual(new[] { broken }, report.IncompleteArchivePaths.ToArray());
        var issue = report.Issues.Single();
        Assert.AreEqual(ScriptArchiveIssueCode.NotZip, issue.Code);
        Assert.AreEqual(ScriptArchiveIssueStage.Structure, issue.Stage);
    }

    [TestMethod]
    public async Task Zip64IsRejectedBeforeOpening()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        // 只有 Zip64 定位记录 + 尾部记录，足以让预检认出 Zip64。
        var bytes = new byte[20 + 22];
        BitConverter.GetBytes(0x07064B50u).CopyTo(bytes, 0);
        BitConverter.GetBytes(0x06054B50u).CopyTo(bytes, 20);
        temp.WriteBytes("Mods/big.ts4script", bytes);

        var report = await CreateScanner().ScanAsync(Request(mods));

        Assert.AreEqual(ScriptArchiveIssueCode.Zip64Unsupported, report.Issues.Single().Code);
        Assert.AreEqual(1, report.IncompleteArchiveCount);
    }

    [TestMethod]
    public async Task EntryCountIsCheckedBeforeZipArchiveLoadsTheDirectory()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        WriteScript(temp, "Mods/many.ts4script",
            ("a.pyc", Pyc("a")), ("b.pyc", Pyc("b")), ("c.pyc", Pyc("c")));

        var report = await CreateScanner().ScanAsync(Request(mods, new ScriptModuleLimits(MaxEntriesPerArchive: 2)));

        var issue = report.Issues.Single();
        Assert.AreEqual(ScriptArchiveIssueCode.EntryCountExceedsLimit, issue.Code);
        Assert.AreEqual(1, report.IncompleteArchiveCount);
    }

    [TestMethod]
    public async Task ArchiveOverSizeLimitIsNotOpened()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        WriteScript(temp, "Mods/a.ts4script", ("a.pyc", Pyc(new string('x', 4096))));

        var report = await CreateScanner().ScanAsync(Request(mods, new ScriptModuleLimits(MaxArchiveBytes: 64)));

        Assert.AreEqual(ScriptArchiveIssueCode.TooLarge, report.Issues.Single().Code);
    }

    [TestMethod]
    public async Task OversizedModuleIsCountedByActualOutputNotDeclaredSize()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        // 高度可压缩的内容：压缩后很小，解压后超过单条上限，相当于一个小型「解压炸弹」。
        var bomb = Pyc(new string('0', 64 * 1024));
        WriteScript(temp, "Mods/a.ts4script", ("lib.pyc", bomb), ("other.pyc", Pyc("fine")));
        WriteScript(temp, "Mods/b.ts4script", ("lib.pyc", Pyc("small")));

        var report = await CreateScanner().ScanAsync(Request(mods, new ScriptModuleLimits(MaxModuleBytes: 1024)));

        // 只这一个模块没比较，归档本身照常分析。
        Assert.AreEqual(2, report.AnalyzedArchiveCount);
        var collision = report.Collisions.Single();
        Assert.AreEqual(ScriptModuleVerdict.Incomplete, collision.Verdict);
        var failed = collision.Uncompared.Single();
        Assert.AreEqual(ScriptArchiveIssueCode.EntrySizeExceedsLimit, failed.Issue!.Code);
        Assert.AreEqual("lib.pyc", failed.Issue.EntryName);
        Assert.IsTrue(report.ProcessedBytes < 64 * 1024, $"processed {report.ProcessedBytes}");
    }

    [TestMethod]
    public async Task SharedBudgetStopsTheWholeScan()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        WriteScript(temp, "Mods/a.ts4script", ("lib.pyc", Pyc(new string('a', 2000))));
        WriteScript(temp, "Mods/b.ts4script", ("lib.pyc", Pyc(new string('b', 2000))));

        var report = await CreateScanner().ScanAsync(Request(mods, new ScriptModuleLimits(
            MaxTotalProcessedBytes: 2500,
            MaxDegreeOfParallelism: 1)));

        Assert.IsTrue(report.ProcessedBytes <= 2500, $"processed {report.ProcessedBytes}");
        var collision = report.Collisions.Single();
        Assert.AreEqual(ScriptModuleVerdict.Incomplete, collision.Verdict);
        Assert.IsTrue(collision.Uncompared.Any(occurrence =>
            occurrence.Issue!.Code == ScriptArchiveIssueCode.BudgetExhausted));
    }

    [TestMethod]
    public async Task CorruptEntryOnlyAffectsThatModule()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var path = WriteScript(temp, "Mods/a.ts4script",
            ("broken.pyc", Pyc(new string('q', 5000) + Guid.NewGuid())),
            ("fine.pyc", Pyc("fine")));
        WriteScript(temp, "Mods/b.ts4script", ("broken.pyc", Pyc("other")), ("fine.pyc", Pyc("fine")));

        // 把第一个条目的压缩数据中段改坏，结构（中央目录）不动。
        var bytes = File.ReadAllBytes(path);
        for (var offset = 60; offset < 90; offset++)
        {
            bytes[offset] ^= 0xFF;
        }
        File.WriteAllBytes(path, bytes);

        var report = await CreateScanner().ScanAsync(Request(mods));

        Assert.AreEqual(2, report.AnalyzedArchiveCount);
        var broken = report.Collisions.Single(collision => collision.ModuleName == "broken");
        var fine = report.Collisions.Single(collision => collision.ModuleName == "fine");
        Assert.AreEqual(ScriptArchiveIssueCode.EntryCorrupt, broken.Uncompared.Single().Issue!.Code);
        Assert.AreEqual(ScriptModuleVerdict.Identical, fine.Verdict);
    }
}
