using System.Text.Json;

namespace GoogleSlidesNdiPresenter;

public sealed class RecentPresentation
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int SlideCount { get; set; }
    public DateTime LastOpened { get; set; }
}

public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Persisted user settings, stored in %APPDATA%\GoogleSlidesNdiPresenter\settings.json.</summary>
public sealed class AppSettings
{
    public string LastUrl { get; set; } = "";
    public float NotesFontSize { get; set; } = 48;
    public bool AutoFitNotes { get; set; }
    public int ApiPort { get; set; } = 8080;
    public string SlidesNdiName { get; set; } = "GSlides Slides";
    public string NotesNdiName { get; set; } = "GSlides Notes";
    /// <summary>NDI discovery server(s), comma separated ("host" or "host:port"). Empty = automatic discovery.</summary>
    public string NdiDiscoveryServer { get; set; } = "";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int FrameRate { get; set; } = 30;
    /// <summary>Where slide audio plays: "default" (system default device), "none", or a device id.</summary>
    public string AudioOutputDevice { get; set; } = AudioRouter.DefaultDevice;
    public int AudioOutputVolume { get; set; } = 100;
    /// <summary>Optional second device for monitoring (e.g. headphones): "none", "default" or a device id.</summary>
    public string AudioMonitorDevice { get; set; } = AudioRouter.NoDevice;
    public int AudioMonitorVolume { get; set; } = 100;
    /// <summary>Allow the REST API / web remote to be reached from other devices (otherwise localhost only).</summary>
    public bool RemoteAccess { get; set; } = true;
    public List<RecentPresentation> Recent { get; set; } = [];
    public WindowPlacement? Window { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoogleSlidesNdiPresenter", "settings.json");

    /// <summary>The settings file in use; overridden with the --settings command-line option.</summary>
    public static string FilePath { get; set; } = DefaultFilePath;

    // Where settings lived before the app was renamed from "GSlideNdi".
    private static readonly string LegacyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GSlideNdi", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (FilePath == DefaultFilePath && !File.Exists(FilePath) && File.Exists(LegacyFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(LegacyFilePath, FilePath);
            }
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings file: fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Non-fatal.
        }
    }
}
