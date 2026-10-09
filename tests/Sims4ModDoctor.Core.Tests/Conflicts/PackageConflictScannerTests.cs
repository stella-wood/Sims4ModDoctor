using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Conflicts;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Core.Packages;

namespace Sims4ModDoctor.Core.Tests.Conflicts;

[TestClass]
public sealed class PackageConflictScannerTests
{
    private static readonly ResourceKey Hair = new(0x034AEECB, 0x00000000, 0x0000_0000_AAAA_0001);
    private static readonly ResourceKey Shirt = new(0x034AEECB, 0x00000000, 0x0000_0000_AAAA_0002);
    private static readonly ResourceKey Tuning = new(0x0333406C, 0x00000000, 0x0000_0000_BBBB_0001);
    private static readonly ResourceKey Lonely = new(0x00B2D882, 0x00000000, 0x0000_0000_CCCC_0001);

    private static PackageConflictScanner CreateScanner(
        FakePackageIndexReader reader,
        IFileSystemAccess? fileSystem = null) =>
        new(fileSystem ?? new PhysicalFileSystemAccess(), reader);

    private static string Placeholder(TempDirectory temp, params string[] parts) =>
        temp.WriteFile(Path.Combine(parts), "fake reader ignores content");

    [TestMethod]
    public async Task SameTgiInTwoPackagesProducesOneCandidate()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var first = Placeholder(temp, "Mods", "a.package");
        var second = Placeholder(temp, "Mods", "b.package");
        var reader = new FakePackageIndexReader()
            .WithIndex("a.package", Hair, Lonely)
            .WithIndex("b.package", Hair);

        var report = await CreateScanner(reader).ScanAsync(
            new PackageConflictScanRequest([new ScanSource("mods", mods, Label: "Mods")]));

        Assert.AreEqual(2, report.DiscoveredPackageCount);
        Assert.AreEqual(2, report.AnalyzedPackageCount);
        Assert.AreEqual(0, report.IncompletePackageCount);
        Assert.AreEqual(1, report.Candidates.Count);

        var candidate = report.Candidates[0];
        Assert.AreEqual(Hair, candidate.Key);
        Assert.AreEqual(2, candidate.PackageCount);
        CollectionAssert.AreEqual(
            new[] { first, second },
            candidate.Occurrences.Select(occurrence => occurrence.PackagePath).ToArray());

        var occurrence = candidate.Occurrences[0];
        Assert.AreEqual(0, occurrence.Ordinal);
        Assert.AreEqual(100L, occurrence.ContentSize);
        Assert.AreEqual(PackageCompression.Zlib, occurrence.Compression);
        Assert.AreEqual("ZLIB", occurrence.CompressionRaw);
        CollectionAssert.AreEqual(new[] { "mods" }, occurrence.SourceIds.ToArray());
    }

    [TestMethod]
    public async Task TgiInOnlyOnePackageProducesNoCandidate()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        Placeholder(temp, "Mods", "a.package");
        Placeholder(temp, "Mods", "b.package");
        var reader = new FakePackageIndexReader()
            .WithIndex("a.package", Hair)
            .WithIndex("b.package", Shirt);

        var report = await CreateScanner(reader).ScanAsync(
            new PackageConflictScanRequest([new ScanSource("mods", mods)]));

        Assert.AreEqual(2, report.AnalyzedPackageCount);
        Assert.AreEqual(0, report.Candidates.Count);
    }

    [TestMethod]
    public async Task OnePackageCanTakePartInSeveralCandidateGroups()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var merged = Placeholder(temp, "Mods", "merged.package");
        Placeholder(temp, "Mods", "hair.package");
        Placeholder(temp, "Mods", "tuning.package");
        var reader = new FakePackageIndexReader()
            .WithIndex("merged.package", Tuning, Hair, Lonely)
            .WithIndex("hair.package", Hair)
            .WithIndex("tuning.package", Tuning);

        var report = await CreateScanner(reader).ScanAsync(
            new PackageConflictScanRequest([new ScanSource("mods", mods)]));

        // 按 TGI 排序：Type 0x0333406C 在 0x034AEECB 之前。
        CollectionAssert.AreEqual(
            new[] { Tuning, Hair },
            report.Candidates.Select(candidate => candidate.Key).ToArray());
        Assert.IsTrue(report.Candidates.All(candidate =>
            candidate.Occurrences.Any(occurrence => occurrence.PackagePath == merged)));

        // 同一个 package 在不同组里保留各自的原始 ordinal。
        Assert.AreEqual(0, report.Candidates[0].Occurrences.Single(o => o.PackagePath == merged).Ordinal);
        Assert.AreEqual(1, report.Candidates[1].Occurrences.Single(o => o.PackagePath == merged).Ordinal);
    }

    [TestMethod]
    public async Task SameSourceConfiguredTwiceIsReadOnce()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        Placeholder(temp, "Mods", "a.package");
        Placeholder(temp, "Mods", "b.package");
        var reader = new FakePackageIndexReader()
            .WithIndex("a.package", Hair)
            .WithIndex("b.package", Hair);

        var report = await CreateScanner(reader).ScanAsync(new PackageConflictScanRequest(
        [
            new ScanSource("mods", mods),
            new ScanSource("mods-again", mods + Path.DirectorySeparatorChar),
        ]));

        Assert.AreEqual(2, report.DiscoveredPackageCount);
        Assert.IsTrue(reader.ReadCounts.Values.All(count => count == 1));
        Assert.AreEqual(1, report.Candidates.Count);
        Assert.AreEqual(2, report.Candidates[0].Occurrences.Count);
        CollectionAssert.AreEqual(
            new[] { "mods", "mods-again" },
            report.Candidates[0].Occurrences[0].SourceIds.ToArray());
    }

    [TestMethod]
    public async Task OverlappingParentAndChildSourcesReadEachFileOnce()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var child = temp.CreateDirectory("Mods", "发型");
        Placeholder(temp, "Mods", "outside.package");
        var inside = Placeholder(temp, "Mods", "发型", "inside.package");
        var reader = new FakePackageIndexReader()
            .WithIndex("outside.package", Hair)
            .WithIndex("inside.package", Hair);

        var report = await CreateScanner(reader).ScanAsync(new PackageConflictScanRequest(
        [
            new ScanSource("mods", mods, Order: 0),
            new ScanSource("hair", child, Order: 1),
        ]));

        Assert.AreEqual(2, report.DiscoveredPackageCount);
        Assert.AreEqual(2, reader.ReadCounts.Count);
        Assert.IsTrue(reader.ReadCounts.Values.All(count => count == 1));
        Assert.AreEqual(1, report.Candidates.Count);
        Assert.AreEqual(2, report.Candidates[0].Occurrences.Count);
        CollectionAssert.AreEqual(
            new[] { "mods", "hair" },
            report.Candidates[0].Occurrences.Single(o => o.PackagePath == inside).SourceIds.ToArray());
    }

    [TestMethod]
    public async Task FailedPackageIsCountedIncompleteAndOthersContinue()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        Placeholder(temp, "Mods", "a.package");
        var broken = Placeholder(temp, "Mods", "broken.package");
        var crashing = Placeholder(temp, "Mods", "crashing.package");
        Placeholder(temp, "Mods", "z.package");
        var reader = new FakePackageIndexReader()
            .WithIndex("a.package", Hair)
            .WithFailure("broken.package")
            .WithException("crashing.package", new InvalidDataException("boom"))
            .WithIndex("z.package", Hair);

        var report = await CreateScanner(reader).ScanAsync(
            new PackageConflictScanRequest([new ScanSource("mods", mods)]));

        Assert.AreEqual(4, report.DiscoveredPackageCount);
        Assert.AreEqual(2, report.AnalyzedPackageCount);
        CollectionAssert.AreEqual(new[] { broken, crashing }, report.IncompletePackagePaths.ToArray());
        Assert.AreEqual(1, report.Candidates.Count);

        var readIssues = report.Issues
            .Where(issue => issue.Stage == PackageConflictScanIssueStage.IndexRead)
            .ToArray();
        Assert.AreEqual(2, readIssues.Length);
        Assert.AreEqual(PackageReadIssueCode.MagicMismatch, readIssues[0].Code);
        Assert.AreEqual(PackageReadStage.Precheck, readIssues[0].ReadStage);
        Assert.AreEqual(PackageConflictScanIssueCode.UnexpectedReadError, readIssues[1].Code);
        StringAssert.Contains(readIssues[1].Detail, "InvalidDataException");
    }

    [TestMethod]
    public async Task InaccessibleSourceDoesNotAffectAccessibleSource()
    {
        using var temp = new TempDirectory();
        var good = temp.CreateDirectory("good");
        var blocked = temp.CreateDirectory("blocked");
        Placeholder(temp, "good", "a.package");
        Placeholder(temp, "good", "b.package");
        Placeholder(temp, "blocked", "c.package");
        var missing = Path.Combine(temp.Path, "missing");
        var reader = new FakePackageIndexReader()
            .WithIndex("a.package", Hair)
            .WithIndex("b.package", Hair)
            .WithIndex("c.package", Hair);

        var report = await CreateScanner(
                reader,
                new FaultingFileSystemAccess(new PhysicalFileSystemAccess(), blocked))
            .ScanAsync(new PackageConflictScanRequest(
            [
                new ScanSource("good", good),
                new ScanSource("blocked", blocked),
                new ScanSource("missing", missing),
            ]));

        Assert.AreEqual(2, report.DiscoveredPackageCount);
        Assert.AreEqual(1, report.Candidates.Count);
        Assert.AreEqual(2, report.Candidates[0].Occurrences.Count);
        Assert.IsTrue(report.Issues.Any(issue =>
            issue.Code == "source-not-found" && issue.SourceId == "missing"));
        Assert.IsTrue(report.Issues.Any(issue =>
            issue.Code == "directory-enumeration-failed" && issue.SourceId == "blocked"));
    }

    [TestMethod]
    public async Task CancellationDuringReadingPropagatesAndNeverReportsCompleted()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        for (var index = 0; index < 8; index++)
        {
            Placeholder(temp, "Mods", $"{index}.package");
        }

        using var cancellation = new CancellationTokenSource();
        var reader = new FakePackageIndexReader
        {
            OnRead = name =>
            {
                if (name == "3.package")
                {
                    cancellation.Cancel();
                }
            },
        };
        var phases = new List<PackageConflictScanPhase>();
        var progress = new SynchronousProgress<PackageConflictScanProgress>(p => phases.Add(p.Phase));

        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateScanner(reader).ScanAsync(
            new PackageConflictScanRequest([new ScanSource("mods", mods)], MaxDegreeOfParallelism: 2),
            progress,
            cancellation.Token));

        CollectionAssert.DoesNotContain(phases, PackageConflictScanPhase.Completed);
    }

    [TestMethod]
    public async Task CancellationBeforeStartPropagates()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateScanner(new FakePackageIndexReader())
            .ScanAsync(new PackageConflictScanRequest([new ScanSource("mods", mods)]), cancellation.Token));
    }

    [TestMethod]
    public async Task ResultOrderDoesNotDependOnDiscoveryOrCompletionOrder()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var names = new[] { "e.package", "d.package", "c.package", "b.package", "a.package", "sub/f.package" };
        foreach (var name in names)
        {
            temp.WriteFile(Path.Combine("Mods", name), "x");
        }

        FakePackageIndexReader CreateReader(bool staggered)
        {
            var reader = new FakePackageIndexReader();
            for (var index = 0; index < names.Length; index++)
            {
                var name = Path.GetFileName(names[index]);
                // 每个 package 都含 Hair；另外两两共享一个 TGI，并且故意让 key 的出现顺序各不相同。
                reader.WithIndex(
                    name,
                    index % 2 == 0
                        ? [Hair, new ResourceKey(0x1, 0, (ulong)(index / 2))]
                        : [new ResourceKey(0x1, 0, (ulong)(index / 2)), Hair]);
                if (staggered)
                {
                    // 路径越靠前完成得越晚。
                    reader.WithDelay(name, TimeSpan.FromMilliseconds(10 * (names.Length - index)));
                }
            }

            return reader;
        }

        static string Fingerprint(PackageConflictScanReport report) => string.Join(
            "\n",
            report.Candidates.SelectMany(candidate => candidate.Occurrences.Select(occurrence =>
                $"{candidate.Key.Tgi}|{occurrence.PackagePath}|{occurrence.Ordinal}")));

        var sequential = await CreateScanner(CreateReader(staggered: false)).ScanAsync(
            new PackageConflictScanRequest([new ScanSource("mods", mods)], MaxDegreeOfParallelism: 1));
        var shuffled = await CreateScanner(
                CreateReader(staggered: true),
                new ReversedEnumerationFileSystem(new PhysicalFileSystemAccess()))
            .ScanAsync(new PackageConflictScanRequest([new ScanSource("mods", mods)], MaxDegreeOfParallelism: 4));

        Assert.AreEqual(4, sequential.Candidates.Count);
        Assert.AreEqual(Fingerprint(sequential), Fingerprint(shuffled));
    }

    [TestMethod]
    public async Task ExtensionMatchIsCaseInsensitiveAndOtherFilesAreIgnored()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        Placeholder(temp, "Mods", "upper.PACKAGE");
        Placeholder(temp, "Mods", "mixed.Package");
        Placeholder(temp, "Mods", "script.ts4script");
        Placeholder(temp, "Mods", "notes.txt");
        var reader = new FakePackageIndexReader()
            .WithIndex("upper.PACKAGE", Hair)
            .WithIndex("mixed.Package", Hair);

        var report = await CreateScanner(reader).ScanAsync(
            new PackageConflictScanRequest([new ScanSource("mods", mods)]));

        Assert.AreEqual(2, report.DiscoveredPackageCount);
        Assert.AreEqual(1, report.Candidates.Count);
        Assert.AreEqual(2, reader.ReadCounts.Count);
    }

    [TestMethod]
    public async Task ProgressWalksThroughEveryPhaseAndEndsCompleted()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        Placeholder(temp, "Mods", "a.package");
        Placeholder(temp, "Mods", "b.package");
        var reader = new FakePackageIndexReader()
            .WithIndex("a.package", Hair)
            .WithIndex("b.package", Hair);
        var updates = new List<PackageConflictScanProgress>();

        await CreateScanner(reader).ScanAsync(
            new PackageConflictScanRequest([new ScanSource("mods", mods)]),
            new SynchronousProgress<PackageConflictScanProgress>(updates.Add));

        var phases = updates.Select(update => update.Phase).Distinct().ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                PackageConflictScanPhase.Starting,
                PackageConflictScanPhase.Discovering,
                PackageConflictScanPhase.ReadingIndexes,
                PackageConflictScanPhase.Grouping,
                PackageConflictScanPhase.Completed,
            },
            phases);
        Assert.AreEqual(PackageConflictScanPhase.Completed, updates[^1].Phase);
        StringAssert.Contains(updates[^1].Message, "候选冲突");
        Assert.AreEqual(2, updates.Last(u => u.Phase == PackageConflictScanPhase.ReadingIndexes).CompletedItems);
    }

    [TestMethod]
    public async Task ReadsAreBoundedByTheConfiguredParallelism()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var reader = new FakePackageIndexReader();
        for (var index = 0; index < 12; index++)
        {
            Placeholder(temp, "Mods", $"{index:D2}.package");
            reader.WithDelay($"{index:D2}.package", TimeSpan.FromMilliseconds(20));
        }

        var report = await CreateScanner(reader).ScanAsync(
            new PackageConflictScanRequest([new ScanSource("mods", mods)], MaxDegreeOfParallelism: 3));

        Assert.AreEqual(12, report.AnalyzedPackageCount);
        Assert.IsTrue(reader.MaxInFlight <= 3, $"max in flight: {reader.MaxInFlight}");
    }

    [TestMethod]
    public async Task RejectsNonPositiveParallelism()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateScanner(new FakePackageIndexReader())
            .ScanAsync(new PackageConflictScanRequest([new ScanSource("mods", mods)], MaxDegreeOfParallelism: 0)));
    }

    [TestMethod]
    public async Task ReportCollectionsAreReadOnly()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        Placeholder(temp, "Mods", "a.package");
        Placeholder(temp, "Mods", "b.package");
        var reader = new FakePackageIndexReader()
            .WithIndex("a.package", Hair)
            .WithIndex("b.package", Hair);

        var report = await CreateScanner(reader).ScanAsync(
            new PackageConflictScanRequest([new ScanSource("mods", mods)]));

        // 数组也实现 IReadOnlyList，但调用方可以把它转回数组改写；这里要求交出去的都不是数组或 List。
        AssertNotMutable(report.Candidates);
        AssertNotMutable(report.Sources);
        AssertNotMutable(report.Candidates[0].Occurrences);
        AssertNotMutable(report.Candidates[0].Occurrences[0].SourceIds);
        AssertNotMutable(report.IncompletePackagePaths);
        AssertNotMutable(report.Issues);
    }

    private static void AssertNotMutable<T>(IReadOnlyList<T> list)
    {
        Assert.IsFalse(list is T[], $"{typeof(T).Name} list is an array");
        Assert.IsFalse(list is List<T>, $"{typeof(T).Name} list is a List<T>");
    }

    /// <summary><see cref="Progress{T}"/> 会把回调投递到线程池，测试里需要同步收集。</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly object _gate = new();

        public void Report(T value)
        {
            lock (_gate)
            {
                handler(value);
            }
        }
    }
}
