using System.IO;
using System.Text.Json;

namespace Sims4ModDoctor.Desktop.Services;

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

    private readonly string settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sims4ModDoctor",
        "duplicate-settings.json");

    public DuplicateSettings? Load()
    {
        try
        {
            return File.Exists(settingsPath)
                ? JsonSerializer.Deserialize<DuplicateSettings>(File.ReadAllText(settingsPath), JsonOptions)
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
