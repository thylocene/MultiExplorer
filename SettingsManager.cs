using System;
using System.IO;
using System.Text.Json;

namespace MultiExplorer;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to a JSON file under %APPDATA%.
/// All methods are best-effort: failures are swallowed so they never crash the app.
/// </summary>
public static class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MultiExplorer",
        "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Reads settings from disk. Returns a default <see cref="AppSettings"/> instance
    /// if the file does not exist or cannot be parsed.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* fall through to default */ }

        return new AppSettings();
    }

    /// <summary>
    /// Writes settings to disk, creating the directory if it does not exist.
    /// Silently swallows any I/O or serialisation errors.
    /// </summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (dir != null)
                Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch { /* best-effort */ }
    }
}
