using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Duplicates;

namespace Sims4ModDoctor.Core.Tests;

[TestClass]
public sealed class DuplicateScannerTests
{
    [TestMethod]
    public async Task FindsExactDuplicatesAcrossSourcesAndPrefersModsCopy()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("模拟人生", "Mods");
        var downloads = temp.CreateDirectory("下载");
        var modsCopy = temp.WriteFile(Path.Combine("模拟人生", "Mods", "chair.package"), "same");
        var externalCopy = temp.WriteFile(Path.Combine("下载", "chair-copy.package"), "same");
        temp.WriteFile(Path.Combine("下载", "same-size.package"), "diff");
        temp.WriteFile(Path.Combine("下载", "unique.package"), "a unique file size");
        temp.WriteFile(Path.Combine("下载", "ignored.txt"), "same");

        var scanner = DuplicateScanner.CreateDefault();
        var report = await scanner.ScanAsync(new DuplicateScanRequest(
            [new ScanSource("mods", mods), new ScanSource("downloads", downloads)],
            ModsRoot: mods,
            IncludedExtensions: [".package"]));

        Assert.AreEqual(4, report.DiscoveredFileCount);
        Assert.AreEqual(3, report.HashedFileCount);
        Assert.AreEqual(1, report.Groups.Count);
        Assert.AreEqual(DuplicateSection.Ordinary, report.Groups[0].Section);
        Assert.AreEqual(modsCopy, report.Groups[0].SuggestedKeepPath);
        CollectionAssert.AreEqual(new[] { externalCopy }, report.Groups[0].SuggestedDeletePaths.ToArray());
        Assert.AreEqual(0, report.Issues.Count);
    }

    [TestMethod]
    public async Task DisabledSourceIsNotScanned()
    {
        using var temp = new TempDirectory();
        var enabled = temp.CreateDirectory("enabled");
        var disabled = temp.CreateDirectory("disabled");
        temp.WriteFile(Path.Combine("enabled", "one.package"), "same");
        temp.WriteFile(Path.Combine("disabled", "two.package"), "same");

        var report = await DuplicateScanner.CreateDefault().ScanAsync(new DuplicateScanRequest(
            [new ScanSource("enabled", enabled), new ScanSource("disabled", disabled, Enabled: false)]));

        Assert.AreEqual(1, report.DiscoveredFileCount);
        Assert.AreEqual(0, report.Groups.Count);
        Assert.AreEqual(1, report.Sources.Count);
        Assert.AreEqual("enabled", report.Sources[0].Id);
    }

    [TestMethod]
    public async Task OverlappingParentAndChildReadEachFileOnceAndCreateFocusSection()
    {
        using var temp = new TempDirectory();
        var parent = temp.CreateDirectory("Mods");
        var focus = temp.CreateDirectory("Mods", "家具");
        var outsideFile = temp.WriteFile(Path.Combine("Mods", "outside.package"), "duplicate");
        var focusFile = temp.WriteFile(Path.Combine("Mods", "家具", "inside.package"), "duplicate");
        var fileSystem = new CountingFileSystemAccess(new PhysicalFileSystemAccess());
        var scanner = new DuplicateScanner(
            fileSystem,
            new StableFileHasher(fileSystem, TimeSpan.Zero),
            new DuplicateSelectionService());

        var report = await scanner.ScanAsync(new DuplicateScanRequest(
            [new ScanSource("parent", parent), new ScanSource("focus", focus)]));

        Assert.AreEqual(2, report.DiscoveredFileCount);
        Assert.AreEqual(1, report.Groups.Count);
        Assert.AreEqual(DuplicateSection.FocusCrossScope, report.Groups[0].Section);
        Assert.AreEqual(1, fileSystem.OpenCounts[outsideFile]);
        Assert.AreEqual(1, fileSystem.OpenCounts[focusFile]);

        var focused = report.Groups[0].Files.Single(file => file.Path == focusFile);
        CollectionAssert.AreEqual(new[] { "parent", "focus" }, focused.SourceIds.ToArray());
        CollectionAssert.AreEqual(new[] { "focus" }, focused.PrimarySourceIds.ToArray());
    }

    [TestMethod]
    public async Task DirectoryPermissionFailureDoesNotDiscardOtherSources()
    {
        using var temp = new TempDirectory();
        var blocked = temp.CreateDirectory("blocked");
        var available = temp.CreateDirectory("available");
        temp.WriteFile(Path.Combine("available", "one.package"), "one");
        var physical = new PhysicalFileSystemAccess();
        var fileSystem = new FaultingFileSystemAccess(physical, blocked);
        var scanner = new DuplicateScanner(
            fileSystem,
            new StableFileHasher(fileSystem, TimeSpan.Zero),
            new DuplicateSelectionService());

        var report = await scanner.ScanAsync(new DuplicateScanRequest(
            [new ScanSource("blocked", blocked), new ScanSource("available", available)]));

        Assert.AreEqual(1, report.DiscoveredFileCount);
        Assert.AreEqual(1, report.Issues.Count);
        Assert.AreEqual("directory-enumeration-failed", report.Issues[0].Code);
        Assert.AreEqual(blocked, report.Issues[0].Path);
    }

    [TestMethod]
    public async Task ReparsePointDirectoryIsReportedAndNotFollowed()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("root");
        var reparse = temp.CreateDirectory("root", "linked");
        temp.WriteFile(Path.Combine("root", "linked", "hidden.package"), "same");
        var physical = new PhysicalFileSystemAccess();
        var fileSystem = new ReparsePointFileSystemAccess(physical, reparse);
        var scanner = new DuplicateScanner(
            fileSystem,
            new StableFileHasher(fileSystem, TimeSpan.Zero),
            new DuplicateSelectionService());

        var report = await scanner.ScanAsync(
            new DuplicateScanRequest([new ScanSource("root", root)]));

        Assert.AreEqual(0, report.DiscoveredFileCount);
        Assert.AreEqual(1, report.Issues.Count);
        Assert.AreEqual("reparse-point-skipped", report.Issues[0].Code);
    }

    [TestMethod]
    public async Task ProducesDeterministicGroupAndFileOrdering()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("root");
        temp.WriteFile(Path.Combine("root", "z.package"), "aaaa");
        temp.WriteFile(Path.Combine("root", "a.package"), "aaaa");
        temp.WriteFile(Path.Combine("root", "d.package"), "bbbbb");
        temp.WriteFile(Path.Combine("root", "c.package"), "bbbbb");
        var scanner = DuplicateScanner.CreateDefault();
        var request = new DuplicateScanRequest([new ScanSource("root", root)]);

        var first = await scanner.ScanAsync(request);
        var second = await scanner.ScanAsync(request);

        CollectionAssert.AreEqual(
            first.Groups.Select(group => $"{group.Sha256}:{string.Join('|', group.Files.Select(file => file.Path))}").ToArray(),
            second.Groups.Select(group => $"{group.Sha256}:{string.Join('|', group.Files.Select(file => file.Path))}").ToArray());
        CollectionAssert.AreEqual(
            first.Groups[0].Files.Select(file => file.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            first.Groups[0].Files.Select(file => file.Path).ToArray());
    }

    [TestMethod]
    public async Task SupportsLongUnicodePaths()
    {
        using var temp = new TempDirectory();
        var parts = Enumerable.Range(0, 10).Select(index => $"很长的目录_{index:D2}_abcdefghijkl").ToArray();
        var directory = temp.CreateDirectory(parts);
        var relativeDirectory = Path.GetRelativePath(temp.Path, directory);
        temp.WriteFile(Path.Combine(relativeDirectory, "副本一.package"), "长路径重复内容");
        temp.WriteFile(Path.Combine(relativeDirectory, "副本二.package"), "长路径重复内容");
        Assert.IsGreaterThan(260, directory.Length);

        var report = await DuplicateScanner.CreateDefault().ScanAsync(
            new DuplicateScanRequest([new ScanSource("long", directory)]));

        Assert.AreEqual(1, report.Groups.Count);
        Assert.AreEqual(0, report.Issues.Count);
    }

    [TestMethod]
    public async Task ReportsDiscoveryHashingAndCompletionProgress()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("root");
        temp.WriteFile(Path.Combine("root", "one.package"), "same");
        temp.WriteFile(Path.Combine("root", "two.package"), "same");
        var updates = new List<DuplicateScanProgress>();

        var report = await DuplicateScanner.CreateDefault().ScanAsync(
            new DuplicateScanRequest([new ScanSource("root", root)]),
            new InlineProgress<DuplicateScanProgress>(updates.Add));

        Assert.AreEqual(1, report.Groups.Count);
        CollectionAssert.Contains(updates.Select(update => update.Phase).ToList(), DuplicateScanPhase.Starting);
        CollectionAssert.Contains(updates.Select(update => update.Phase).ToList(), DuplicateScanPhase.Discovering);
        CollectionAssert.Contains(updates.Select(update => update.Phase).ToList(), DuplicateScanPhase.Hashing);
        Assert.AreEqual(DuplicateScanPhase.Completed, updates[^1].Phase);
        Assert.AreEqual(2, updates[^1].DiscoveredFileCount);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
