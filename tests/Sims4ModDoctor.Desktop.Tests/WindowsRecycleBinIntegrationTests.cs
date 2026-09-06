using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModDoctor.Desktop.Services;

namespace Sims4ModDoctor.Desktop.Tests;

[TestClass]
public sealed class WindowsRecycleBinIntegrationTests
{
    [TestMethod]
    [TestCategory("WindowsIntegration")]
    public async Task UnicodeFileCanRoundTripThroughWindowsRecycleBin()
    {
        if (Environment.GetEnvironmentVariable("SIMS4_MOD_DOCTOR_RUN_RECYCLE_BIN_TEST") != "1")
        {
            Assert.Inconclusive("Set SIMS4_MOD_DOCTOR_RUN_RECYCLE_BIN_TEST=1 to run the real Recycle Bin test.");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"Sims4ModDoctor-Recycle-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "中文回收站验证.package");
        const string expected = "Sims 4 Mod Doctor recycle bin round trip";
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, expected);

        try
        {
            var adapter = new WindowsRecycleBinAdapter();
            Assert.IsTrue(adapter.CanRecycle(path));

            var deleted = await adapter.MoveToRecycleBinAsync([path]);
            Assert.AreEqual(0, deleted.Failures.Count, string.Join(Environment.NewLine, deleted.Failures));
            Assert.AreEqual(1, deleted.CompletedItems.Count);
            Assert.IsFalse(File.Exists(path));

            var restored = await adapter.RestoreAsync(deleted.CompletedItems);
            Assert.AreEqual(0, restored.Failures.Count, string.Join(Environment.NewLine, restored.Failures));
            Assert.AreEqual(1, restored.CompletedPaths.Count);
            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual(expected, await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
