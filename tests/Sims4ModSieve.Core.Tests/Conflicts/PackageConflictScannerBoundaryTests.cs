using System.Collections;
using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;

namespace Sims4ModSieve.Core.Tests.Conflicts;

[TestClass]
public sealed class PackageConflictScannerBoundaryTests
{
    [TestMethod]
    public async Task OutOfMemoryMustPropagate()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        temp.WriteFile(Path.Combine("Mods", "a.package"), "x");
        var reader = new FakePackageIndexReader().WithException("a.package", new OutOfMemoryException("simulated"));
        var scanner = new PackageConflictScanner(new PhysicalFileSystemAccess(), reader);
        var progress = new PhaseRecorder();
        await Assert.ThrowsAsync<OutOfMemoryException>(() => scanner.ScanAsync(
            new PackageConflictScanRequest([new ScanSource("mods", mods)]), progress));
        Assert.IsFalse(progress.Phases.Contains(PackageConflictScanPhase.Completed));
    }

    [TestMethod]
    public async Task SourcesMustBeReadOnly()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var scanner = new PackageConflictScanner(new PhysicalFileSystemAccess(), new FakePackageIndexReader());
        var report = await scanner.ScanAsync(new PackageConflictScanRequest([new ScanSource("mods", mods)]));
        Assert.IsFalse(report.Sources is ScanSourceSnapshot[], "Sources exposes a writable array.");
        var list = (IList<ScanSourceSnapshot>)report.Sources;
        Assert.Throws<NotSupportedException>(() => list[0] = list[0] with { Label = "changed" });
        Assert.AreEqual(mods, report.Sources[0].Label);
    }

    [TestMethod]
    public async Task CancellationDuringOneLargeIndexMustStopEnumerationPromptly()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        temp.WriteFile(Path.Combine("Mods", "a.package"), "x");
        using var cts = new CancellationTokenSource();
        var entries = new CancelingEntries(cts);
        var scanner = new PackageConflictScanner(new PhysicalFileSystemAccess(), new Reader(entries));
        var progress = new PhaseRecorder();
        await Assert.ThrowsAsync<OperationCanceledException>(() => scanner.ScanAsync(
            new PackageConflictScanRequest([new ScanSource("mods", mods)]), progress, cts.Token));
        Assert.IsTrue(entries.Visited < 1024, $"Enumerated {entries.Visited} resources after cancellation at the first entry.");
        Assert.IsTrue(progress.Phases.Contains(PackageConflictScanPhase.Grouping));
        Assert.IsFalse(progress.Phases.Contains(PackageConflictScanPhase.Completed));
    }

    private sealed class PhaseRecorder : IProgress<PackageConflictScanProgress>
    {
        public ConcurrentQueue<PackageConflictScanPhase> Phases { get; } = new();
        public void Report(PackageConflictScanProgress value) => Phases.Enqueue(value.Phase);
    }

    private sealed class Reader(IReadOnlyList<PackageResourceEntry> entries) : IPackageIndexReader
    {
        public Task<PackageReadResult> ReadIndexAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(PackageReadResult.Success(new PackageIndexSummary(
                path, 2, 1, 1, new FileStamp(1, DateTime.UnixEpoch), entries)));
    }

    private sealed class CancelingEntries(CancellationTokenSource cts) : IReadOnlyList<PackageResourceEntry>
    {
        public int Count => 100_000;
        public int Visited { get; private set; }
        public PackageResourceEntry this[int index] => new(new ResourceKey(1, 0, (ulong)index), index, 1, PackageCompression.None, "ForceOff");
        public IEnumerator<PackageResourceEntry> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                if (i == 0) cts.Cancel();
                Visited++;
                yield return this[i];
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
