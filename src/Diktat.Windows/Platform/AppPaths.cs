using System.IO;
namespace Diktat.Windows.Platform;

/// Where Diktat keeps its files.
///
/// Roaming (%APPDATA%\Diktat): settings, history, meetings, secrets — small, per user, may
/// roam with the profile. Local (%LOCALAPPDATA%\Diktat): speech models, logs, state that must
/// not roam (the mute marker).
public static class AppPaths
{
    public const string AppFolderName = "Diktat";

    public static string RoamingDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    public static string LocalDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string SettingsFile => Path.Combine(RoamingDirectory, "settings.json");
    public static string HistoryDirectory => Path.Combine(RoamingDirectory, "History");
    public static string MeetingsDirectory => Path.Combine(RoamingDirectory, "Meetings");
    public static string SecretsDirectory => Path.Combine(RoamingDirectory, "secrets");

    public static string ModelsDirectory => Path.Combine(LocalDirectory, "Models");
    public static string LogsDirectory => Path.Combine(LocalDirectory, "Logs");
    /// Holds the endpoint ID while <see cref="SystemAudioMuter"/> keeps the output muted, so a
    /// crash cannot leave the PC silent: the next start unmutes it.
    public static string MuteMarkerFile => Path.Combine(LocalDirectory, "muted-output.txt");

    /// Temporary audio and the like; safe to delete when nothing records.
    public static string TempDirectory => Path.Combine(Path.GetTempPath(), AppFolderName);

    /// Creates the directories that must exist. Safe to call repeatedly.
    public static void EnsureCreated()
    {
        foreach (var dir in new[] { RoamingDirectory, LocalDirectory, HistoryDirectory, MeetingsDirectory, ModelsDirectory, LogsDirectory })
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// Creates <paramref name="directory"/> if needed and returns it.
    public static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }
}
