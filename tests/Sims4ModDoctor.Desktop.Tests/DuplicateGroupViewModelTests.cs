using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Desktop.ViewModels;

namespace Sims4ModDoctor.Desktop.Tests;

[TestClass]
public sealed class DuplicateGroupViewModelTests
{
    [TestMethod]
    public void GroupSelectionUsesCoreSuggestionAndKeepsExactlyOneFile()
    {
        var modsCopy = CreateFile(@"D:\Mods\chair.package", isInsideMods: true, directoryDepth: 2);
        var externalCopy = CreateFile(@"D:\Downloads\author\chair.package", isInsideMods: false, directoryDepth: 3);
        var secondExternalCopy = CreateFile(@"D:\Downloads\chair-copy.package", isInsideMods: false, directoryDepth: 2);
        var viewModel = CreateViewModel([externalCopy, secondExternalCopy, modsCopy]);

        viewModel.IsSuggestionSelected = true;

        Assert.IsFalse(viewModel.Files.Single(file => file.Path == modsCopy.Path).IsSelected);
        Assert.IsTrue(viewModel.Files.Single(file => file.Path == externalCopy.Path).IsSelected);
        Assert.IsTrue(viewModel.Files.Single(file => file.Path == secondExternalCopy.Path).IsSelected);
        Assert.AreEqual(2, viewModel.SelectedCount);
        Assert.AreEqual(200L, viewModel.SelectedBytes);
        Assert.IsTrue(viewModel.IsSuggestionSelected);
    }

    [TestMethod]
    public void ClearingGroupSelectionClearsEveryFile()
    {
        var viewModel = CreateViewModel([
            CreateFile(@"D:\Mods\one.package", isInsideMods: true, directoryDepth: 2),
            CreateFile(@"D:\Mods\two.package", isInsideMods: true, directoryDepth: 2),
        ]);
        viewModel.IsSuggestionSelected = true;

        viewModel.IsSuggestionSelected = false;

        Assert.IsTrue(viewModel.Files.All(file => !file.IsSelected));
        Assert.AreEqual(0, viewModel.SelectedCount);
        Assert.AreEqual(0L, viewModel.SelectedBytes);
        Assert.IsFalse(viewModel.IsSuggestionSelected);
    }

    [TestMethod]
    public void ManualFileChangesKeepGroupSelectionStateInSync()
    {
        var viewModel = CreateViewModel([
            CreateFile(@"D:\Mods\keep.package", isInsideMods: true, directoryDepth: 3),
            CreateFile(@"D:\Mods\copy-a.package", isInsideMods: true, directoryDepth: 2),
            CreateFile(@"D:\Mods\copy-b.package", isInsideMods: true, directoryDepth: 1),
        ]);
        var suggestedCopies = viewModel.Files.Where(file => !file.IsSuggestedKeep).ToArray();
        var keep = viewModel.Files.Single(file => file.IsSuggestedKeep);

        suggestedCopies[0].IsSelected = true;
        Assert.IsFalse(viewModel.IsSuggestionSelected);

        suggestedCopies[1].IsSelected = true;
        Assert.IsTrue(viewModel.IsSuggestionSelected);

        keep.IsSelected = true;
        Assert.IsTrue(viewModel.IsSuggestionSelected);
    }

    [TestMethod]
    public void SuggestedKeepFileIsDisplayedFirst()
    {
        var shallow = CreateFile(@"D:\Mods\copy.package", isInsideMods: true, directoryDepth: 1);
        var keep = CreateFile(@"D:\Mods\作者\系列\keep.package", isInsideMods: true, directoryDepth: 3);
        var middle = CreateFile(@"D:\Mods\作者\middle.package", isInsideMods: true, directoryDepth: 2);

        var viewModel = CreateViewModel([shallow, middle, keep]);

        Assert.AreEqual(keep.Path, viewModel.Files[0].Path);
        Assert.IsTrue(viewModel.Files[0].IsSuggestedKeep);
    }

    [TestMethod]
    public void PartialManualSelectionDoesNotMarkGroupAsSelected()
    {
        var viewModel = CreateViewModel([
            CreateFile(@"D:\Mods\keep.package", isInsideMods: true, directoryDepth: 3),
            CreateFile(@"D:\Mods\copy-a.package", isInsideMods: true, directoryDepth: 2),
            CreateFile(@"D:\Mods\copy-b.package", isInsideMods: true, directoryDepth: 1),
        ]);

        viewModel.Files[0].IsSelected = true;
        viewModel.Files[1].IsSelected = true;

        Assert.IsFalse(viewModel.IsSuggestionSelected);
    }

    private static DuplicateGroupViewModel CreateViewModel(DuplicateFile[] files)
    {
        var selection = new DuplicateSelectionService().Select(files);
        var group = new DuplicateGroup(
            "test-hash",
            files[0].Size,
            DuplicateSection.Ordinary,
            files,
            selection.KeepPath,
            selection.DeletePaths);

        return new DuplicateGroupViewModel(group, index: 1);
    }

    private static DuplicateFile CreateFile(string path, bool isInsideMods, int directoryDepth)
    {
        return new DuplicateFile(
            path,
            Size: 100,
            LastWriteTimeUtc: DateTime.UnixEpoch,
            SourceIds: ["test"],
            PrimarySourceIds: ["test"],
            IsInsideMods: isInsideMods,
            DirectoryDepth: directoryDepth);
    }
}
