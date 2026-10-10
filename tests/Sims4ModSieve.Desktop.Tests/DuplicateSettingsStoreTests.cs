using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.Services;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class DuplicateSettingsStoreTests
{
    [TestMethod]
    public void RenamePrefersMostRecentLegacyDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"s4ms-settings-{Guid.NewGuid():N}");
        try
        {
            foreach (var name in new[] { "Sim4ModSieve", "Sims4ModDoctor" })
            {
                var path = Path.Combine(directory, name, "duplicate-settings.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(new DuplicateSettings(name, [])));
            }

            Assert.AreEqual("Sim4ModSieve", new DuplicateSettingsStore(directory).Load()?.ModsRoot);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("Sims4ModDoctor")]
    [DataRow("Sim4ModSieve")]
    public void RenameReadsLegacySettingsAndSavesToNewDirectory(string legacyDirectory)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"s4ms-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var legacyPath = Path.Combine(directory, legacyDirectory, "duplicate-settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            var legacy = new DuplicateSettings(@"D:\Mods", [new SavedScanSource(@"D:\Downloads", false)]);
            var legacyJson = JsonSerializer.Serialize(legacy);
            File.WriteAllText(legacyPath, legacyJson);

            var store = new DuplicateSettingsStore(directory);
            var loaded = store.Load();
            Assert.IsNotNull(loaded);
            Assert.AreEqual(legacy.ModsRoot, loaded.ModsRoot);
            CollectionAssert.AreEqual(legacy.Sources.ToArray(), loaded.Sources.ToArray());

            var updated = new DuplicateSettings(@"D:\NewMods", [new SavedScanSource(@"D:\NewMods", true)]);
            store.Save(updated);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "Sims4ModSieve", "duplicate-settings.json")));
            Assert.AreEqual(legacyJson, File.ReadAllText(legacyPath));

            loaded = new DuplicateSettingsStore(directory).Load();
            Assert.IsNotNull(loaded);
            Assert.AreEqual(updated.ModsRoot, loaded.ModsRoot);
            CollectionAssert.AreEqual(updated.Sources.ToArray(), loaded.Sources.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
