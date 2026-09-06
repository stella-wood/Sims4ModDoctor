using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Core.Duplicates;

namespace Sims4ModDoctor.Core.Tests;

[TestClass]
public sealed class DuplicateSelectionServiceTests
{
    [TestMethod]
    public void PrefersModsThenDepthThenStablePath()
    {
        var files = new[]
        {
            File(@"D:\Downloads\very\deep\copy.package", isInsideMods: false, depth: 20),
            File(@"D:\Mods\copy.package", isInsideMods: true, depth: 3),
            File(@"D:\Mods\家具\A.package", isInsideMods: true, depth: 5),
            File(@"D:\Mods\家具\B.package", isInsideMods: true, depth: 5),
        };

        var result = new DuplicateSelectionService().Select(files);

        Assert.AreEqual(@"D:\Mods\家具\A.package", result.KeepPath);
        Assert.AreEqual(3, result.DeletePaths.Count);
    }

    private static DuplicateFile File(string path, bool isInsideMods, int depth) => new(
        path,
        10,
        DateTime.UnixEpoch,
        ["source"],
        ["source"],
        isInsideMods,
        depth);
}
