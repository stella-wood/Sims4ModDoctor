using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Desktop.ViewModels;

namespace Sims4ModDoctor.Desktop.Tests;

[TestClass]
public sealed class DuplicateResultSessionTests
{
    [TestMethod]
    public void TracksSelectionAndExpansionState()
    {
        var session = new DuplicateResultSession();
        var model = CreateGroup(
            File(@"D:\Mods\keep.package", isInsideMods: true, depth: 3),
            File(@"D:\Mods\copy.package", isInsideMods: true, depth: 2),
            File(@"D:\Downloads\copy.package", isInsideMods: false, depth: 2));

        session.Replace(session.PrepareGroups([model]));
        session.ApplySuggestions();

        Assert.IsTrue(session.HasResults);
        Assert.AreEqual(1, session.DuplicateGroupCount);
        Assert.AreEqual(3, session.DuplicateFileCount);
        Assert.AreEqual(2, session.SelectedFileCount);
        Assert.AreEqual("200 B", session.SelectedSizeText);
        Assert.IsTrue(session.ShowCollapseAllIcon);

        session.ToggleAllGroups();

        Assert.IsTrue(session.Groups.All(group => !group.IsExpanded));
        Assert.IsTrue(session.ShowExpandAllIcon);

        session.ClearSelection();
        Assert.AreEqual(0, session.SelectedFileCount);
    }

    [TestMethod]
    public void RemovingPathsRebuildsGroupsThroughTheCoreSelectionRule()
    {
        var keep = File(@"D:\Mods\作者\keep.package", isInsideMods: true, depth: 4);
        var shallow = File(@"D:\Mods\copy.package", isInsideMods: true, depth: 2);
        var external = File(@"D:\Downloads\copy.package", isInsideMods: false, depth: 2);
        var model = CreateGroup(keep, shallow, external);
        var session = new DuplicateResultSession();
        session.Replace(session.PrepareGroups([model]));

        session.RemoveCompletedPaths(
            [model],
            new HashSet<string>([external.Path], StringComparer.OrdinalIgnoreCase));

        Assert.AreEqual(1, session.DuplicateGroupCount);
        Assert.AreEqual(2, session.DuplicateFileCount);
        Assert.AreEqual(keep.Path, session.Groups[0].Model.SuggestedKeepPath);

        session.RemoveCompletedPaths(
            [model],
            new HashSet<string>([external.Path, shallow.Path], StringComparer.OrdinalIgnoreCase));
        Assert.IsFalse(session.HasResults);
    }

    [TestMethod]
    public void ReplacingResultsDetachesThePreviousProjection()
    {
        var session = new DuplicateResultSession();
        var oldProjection = session.PrepareGroups([CreateGroup(
            File(@"D:\Old\one.package"),
            File(@"D:\Old\two.package"))]);
        session.Replace(oldProjection);
        session.Replace(session.PrepareGroups([CreateGroup(
            File(@"D:\New\one.package"),
            File(@"D:\New\two.package"))]));
        var selectionNotifications = 0;
        session.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(DuplicateResultSession.SelectedFileCount))
            {
                selectionNotifications++;
            }
        };

        oldProjection[0].Files[0].IsSelected = true;

        Assert.AreEqual(0, session.SelectedFileCount);
        Assert.AreEqual(0, selectionNotifications);
    }

    [TestMethod]
    public void RestoringSnapshotUsesOnlyFilesThatExistNow()
    {
        using var temporary = new TemporaryDirectory();
        var first = temporary.WriteFile("first.package");
        var second = temporary.WriteFile("second.package");
        var missing = System.IO.Path.Combine(temporary.Path, "missing.package");
        var model = CreateGroup(File(first), File(second), File(missing));
        var session = new DuplicateResultSession();

        session.RestoreExistingPaths([model]);

        Assert.AreEqual(1, session.DuplicateGroupCount);
        CollectionAssert.AreEquivalent(
            new[] { first, second },
            session.Groups[0].Files.Select(file => file.Path).ToArray());
    }

    private static DuplicateGroup CreateGroup(params DuplicateFile[] files)
    {
        var selection = new DuplicateSelectionService().Select(files);
        return new DuplicateGroup(
            "hash",
            files[0].Size,
            DuplicateSection.Ordinary,
            files,
            selection.KeepPath,
            selection.DeletePaths);
    }

    private static DuplicateFile File(
        string path,
        bool isInsideMods = false,
        int depth = 2) => new(
        path,
        100,
        DateTime.UnixEpoch,
        ["source"],
        ["source"],
        isInsideMods,
        depth);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s4md-results-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteFile(string fileName)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            System.IO.File.WriteAllText(path, "same");
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
