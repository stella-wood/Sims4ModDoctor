using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Duplicates;

namespace Sims4ModDoctor.Core.Tests;

[TestClass]
public sealed class StableFileHasherTests
{
    [TestMethod]
    public async Task RetriesOnceWhenFileChangesAndReturnsSecondStableSnapshot()
    {
        var first = new FileStamp(3, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var changed = new FileStamp(4, first.LastWriteTimeUtc.AddSeconds(1));
        var stable = new FileStamp(4, first.LastWriteTimeUtc.AddSeconds(2));
        var fileSystem = new SequencedStampFileSystem("data"u8.ToArray(), first, changed, stable, stable);
        var hasher = new StableFileHasher(fileSystem, TimeSpan.Zero);

        var result = await hasher.HashAsync("file.package");

        Assert.AreEqual(stable, result.Stamp);
        Assert.AreEqual(2, fileSystem.OpenCount);
    }

    [TestMethod]
    public async Task RejectsAFileThatChangesDuringBothAttempts()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fileSystem = new SequencedStampFileSystem(
            "data"u8.ToArray(),
            new FileStamp(4, start),
            new FileStamp(5, start.AddSeconds(1)),
            new FileStamp(5, start.AddSeconds(1)),
            new FileStamp(6, start.AddSeconds(2)));
        var hasher = new StableFileHasher(fileSystem, TimeSpan.Zero);

        await Assert.ThrowsExactlyAsync<FileChangedDuringHashException>(
            () => hasher.HashAsync("file.package"));
        Assert.AreEqual(2, fileSystem.OpenCount);
    }
}
