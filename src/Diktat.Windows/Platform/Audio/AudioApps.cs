using System.Diagnostics;
using System.IO;
using Diktat.AI.Meetings;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Diktat.Windows.Platform;

/// Which apps use audio right now, for the call detection. Reads only what Windows itself shows
/// (the microphone list under Privacy and the audio sessions of the volume mixer), never what an
/// app plays or records. Any thread; errors give an empty list.
public static class AudioApps
{
    private const string MicrophoneConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// Every app that has the microphone open, with whether it also plays sound.
    public static IReadOnlyList<CallDetection.Candidate> Candidates()
    {
        var input = AppsUsingMicrophone();
        if (input.Count == 0) return [];
        var output = AppsPlayingSound();
        return input.Select(app => new CallDetection.Candidate(app, true, output.Contains(app))).ToList();
    }

    /// Windows keeps a key per app that used the microphone; "LastUsedTimeStop" is 0 while the app
    /// still uses it. Store apps are keyed by package family name, desktop programs (under
    /// "NonPackaged") by their path with '#' for '\'. Returned as the package name or the file name.
    public static IReadOnlyList<string> AppsUsingMicrophone()
    {
        var apps = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(MicrophoneConsentKey);
            if (root is null) return apps;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key is null) continue;
                if (name == "NonPackaged")
                {
                    foreach (var program in key.GetSubKeyNames())
                    {
                        using var programKey = key.OpenSubKey(program);
                        if (programKey is not null && InUse(programKey)) apps.Add(Path.GetFileName(program.Replace('#', '\\')).ToLowerInvariant());
                    }
                }
                else if (InUse(key))
                {
                    apps.Add(name.ToLowerInvariant());
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the apps using the microphone failed", ex);
        }
        return apps;
    }

    private static bool InUse(RegistryKey key) =>
        key.GetValue("LastUsedTimeStart") is long start && start != 0
        && key.GetValue("LastUsedTimeStop") is long stop && stop == 0;

    /// The program file names ("chrome.exe") of the active audio sessions on every output.
    public static HashSet<string> AppsPlayingSound()
    {
        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive || session.IsSystemSoundsSession) continue;
                        if (ProgramName((int)session.GetProcessID) is { } program) apps.Add(program);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the audio sessions failed", ex);
        }
        return apps;
    }

    private static string? ProgramName(int processId)
    {
        if (processId <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName.ToLowerInvariant() + ".exe";
        }
        catch
        {
            return null;
        }
    }
}
