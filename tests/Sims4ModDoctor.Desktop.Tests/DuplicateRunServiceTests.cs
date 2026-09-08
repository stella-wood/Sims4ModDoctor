using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Desktop.Services;

namespace Sims4ModDoctor.Desktop.Tests;

[TestClass]
public sealed class DuplicateRunServiceTests
{
    [TestMethod]
    public async Task UsesStableSourceOrderFixedExtensionsAndModsRoot()
    {
        using var temporary = new TemporaryDirectory();
        var downloads = temporary.CreateDirectory("downloads");
        var mods = temporary.CreateDirectory("Mods");
        var externalCopy = temporary.WriteFile("downloads", "chair-copy.package", "same");
        var modsCopy = temporary.WriteFile("Mods", "chair.package", "same");
        temporary.WriteFile("downloads", "ignored.txt", "ignored");
        temporary.WriteFile("Mods", "ignored-copy.txt", "ignored");
        var service = new DuplicateRunService(DuplicateScanner.CreateDefault());

        var report = await service.RunAsync(new DuplicateRunInput(
            [
                new DuplicateRunSource(downloads, "下载"),
                new DuplicateRunSource(mods, "Mods"),
            ],
            mods));

        Assert.AreEqual(2, report.DiscoveredFileCount);
        Assert.AreEqual(1, report.Groups.Count);
        Assert.AreEqual(modsCopy, report.Groups[0].SuggestedKeepPath);
        CollectionAssert.AreEqual(new[] { externalCopy }, report.Groups[0].SuggestedDeletePaths.ToArray());
        CollectionAssert.AreEqual(new[] { "目录 1", "目录 2" }, report.Sources.Select(source => source.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "下载", "Mods" }, report.Sources.Select(source => source.Label).ToArray());
    }

    [TestMethod]
    public async Task PassesCancellationToTheScanner()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new DuplicateRunService(DuplicateScanner.CreateDefault());

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.RunAsync(
            new DuplicateRunInput([new DuplicateRunSource(source, "source")]),
            cancellationToken: cancellation.Token));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s4md-run-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(params string[] parts)
        {
            var path = parts.Aggregate(Path, System.IO.Path.Combine);
            Directory.CreateDirectory(path);
            return path;
        }

        public string WriteFile(string directory, string fileName, string content)
        {
            var path = System.IO.Path.Combine(Path, directory, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
