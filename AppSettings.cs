using System.IO;
using System.Text.Json;

namespace Mirror;

public sealed class AppSettings
{
    /// <summary>"auto" (follow Windows), "zh", "en" or "ja".</summary>
    public string Language { get; set; } = Loc.Auto;
    public bool Mirror { get; set; } = true;
    public bool SaveMirrored { get; set; } = true;
    public bool Grid { get; set; }
    public int TimerSeconds { get; set; }
    public bool VideoMode { get; set; }
    public bool RecordAudio { get; set; } = true;
    /// <summary>"none", "blur", "preset:&lt;id&gt;" or "custom:&lt;file&gt;".</summary>
    public string Background { get; set; } = Backgrounds.None;
    /// <summary>The last background other than "none", which the B key switches back to.</summary>
    public string LastBackground { get; set; } = Backgrounds.Blur;
    public int BlurStrength { get; set; } = 6;
    public string? CameraId { get; set; }
    public string? VideoModeKey { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mirror", "settings.json");

    /// <summary>Demo mode (see <see cref="App.DemoPicture"/>): optional overrides; settings are never saved.</summary>
    public static (string? Language, string? Background)? DemoOverrides { get; set; }

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            settings = new AppSettings();
        }

        if (DemoOverrides is { } demo)
        {
            settings.Language = demo.Language ?? settings.Language;
            settings.Background = demo.Background ?? settings.Background;
            settings.VideoMode = false;
        }
        return settings;
    }

    public void Save()
    {
        if (DemoOverrides is not null)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing preferences is not worth interrupting the camera for.
        }
    }
}
