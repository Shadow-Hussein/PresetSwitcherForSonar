using System.IO;
using System.Text.Json;
using PresetSwitcherForSonar.Models;

namespace PresetSwitcherForSonar.Services;

/// <summary>Loads and atomically saves settings to <c>%APPDATA%\PresetSwitcherForSonar\settings.json</c>.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Directory { get; } = ResolveDirectory();

    /// <summary>
    /// The settings folder, taking over the one from before the rename the first time it is asked for.
    /// The folder is moved rather than copied so there is never a second, stale set of settings for a
    /// leftover old build to pick up.
    /// </summary>
    private static string ResolveDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var current = Path.Combine(appData, "PresetSwitcherForSonar");
        var legacy = Path.Combine(appData, "SonarPresetSwitcher");

        try
        {
            if (!System.IO.Directory.Exists(current) && System.IO.Directory.Exists(legacy))
                System.IO.Directory.Move(legacy, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left where it is; the app starts with defaults rather than not at all.
        }

        return current;
    }

    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings should not stop the app from starting.
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            // Write to a sibling temp file first so an interrupted save cannot truncate the real one.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do; the in-memory settings stay correct for this session.
        }
    }
}
