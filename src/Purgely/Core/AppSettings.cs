using System.IO;
using System.Text.Json;

namespace Purgely.Core;

/// <summary>Purgely's own preferences (Settings page). Stored per user in %LOCALAPPDATA%\Purgely\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Ask GitHub once a day whether a newer Purgely was released. Nothing is downloaded or installed.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Timeline events older than this many days are removed at start. 0 = keep everything.</summary>
    public int TimelineKeepDays { get; set; }
    /// <summary>The welcome guide was shown (or skipped).</summary>
    public bool WelcomeSeen { get; set; }
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>A version the user chose to skip ("Don't remind me about this version").</summary>
    public string SkippedVersion { get; set; } = "";
    /// <summary>The newer release found by the last check (shown again until it is installed or skipped).</summary>
    public string AvailableTag { get; set; } = "";
    public string AvailableUrl { get; set; } = "";

    private static readonly object Gate = new();
    private static AppSettings? _current;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Purgely");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Current
    {
        get
        {
            lock (Gate)
            {
                if (_current is not null) return _current;
                try
                {
                    if (File.Exists(FilePath))
                        _current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                }
                catch (Exception) { }
                return _current ??= new AppSettings();
            }
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception) { /* a setting that can't be saved is simply asked again next time */ }
        }
    }
}
