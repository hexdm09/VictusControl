using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VictusControl.App;

// persisted UI state; hardware state is always re-read from firmware on start
public sealed class AppSettings
{
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool ShowOsd { get; set; } = true;
    public bool KeepAlive { get; set; } = true;
    public int WindowLeft { get; set; } = int.MinValue;
    public int WindowTop { get; set; } = int.MinValue;

    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VictusControl");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch
        {
            // corrupted settings must never prevent startup
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch
        {
            // read-only profile or full disk: keep running with in-memory settings
        }
    }
}
