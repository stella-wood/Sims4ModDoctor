using System.IO;
using System.Text.Json;

namespace Sims4ModSieve.Desktop.Services;

public sealed record SavedScanSource(string Path, bool Enabled);

public sealed record DuplicateSettings(string? ModsRoot, IReadOnlyList<SavedScanSource> Sources);

public interface IDuplicateSettingsStore
{
    DuplicateSettings? Load();

    void Save(DuplicateSettings settings);
}

public sealed class DuplicateSettingsStore : IDuplicateSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string settingsPath;
    private readonly string[] legacySettingsPaths;

    public DuplicateSettingsStore(string? localApplicationData = null)
    {
        var directory = localApplicationData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        settingsPath = Path.Combine(directory, "Sims4ModSieve", "duplicate-settings.json");
        legacySettingsPaths =
        [
            Path.Combine(directory, "Sim4ModSieve", "duplicate-settings.json"),
            Path.Combine(directory, "Sims4ModDoctor", "duplicate-settings.json"),
        ];
    }

    public DuplicateSettings? Load()
    {
        try
        {
            var path = File.Exists(settingsPath) ? settingsPath : legacySettingsPaths.FirstOrDefault(File.Exists);
            return path is not null
                ? JsonSerializer.Deserialize<DuplicateSettings>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(DuplicateSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(settingsPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only settings location should not prevent a scan.
        }
    }
}
