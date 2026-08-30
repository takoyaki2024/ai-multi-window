using System.IO;
using System.Text.Json;

namespace AiMultiWindow;

public sealed class AppSettings
{
    public int LayoutCount { get; set; } = 3;
    public string[] Urls { get; set; } =
    [
        "https://chatgpt.com/",
        "https://gemini.google.com/",
        "https://claude.ai/"
    ];

    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AiMultiWindow");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            settings.LayoutCount = 3;

            var defaults = new AppSettings().Urls;
            var merged = new string[3];
            for (var i = 0; i < merged.Length; i++)
                merged[i] = settings.Urls is not null && i < settings.Urls.Length && !string.IsNullOrWhiteSpace(settings.Urls[i])
                    ? settings.Urls[i]
                    : defaults[i];

            settings.Urls = merged;
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Settings persistence should never prevent the app from closing.
        }
    }
}
