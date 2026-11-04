using System.IO;
using NAudio.CoreAudioApi;

namespace Diktat.Windows.Platform;

/// Mutes the PC's sound output while Diktat records, so music or a video does not run on over
/// the dictation, and restores it exactly as it was (port of the Mac SystemAudioMuter).
///
/// Only mutes when the output was not muted already, and only unmutes the endpoint it muted
/// itself, even when the default output changed meanwhile. A marker file holds the muted
/// endpoint while muted: call <see cref="RestoreAfterCrash"/> at startup so a crash can never
/// leave the PC silent. Restores on <see cref="Dispose"/> and at process exit too.
///
/// Thread-safe; Core Audio calls take a few milliseconds.
public sealed class SystemAudioMuter : IDisposable
{
    private readonly object _gate = new();
    private readonly string _markerFile;
    private string? _mutedDeviceId;
    private bool _disposed;

    public SystemAudioMuter(string? markerFile = null)
    {
        _markerFile = markerFile ?? AppPaths.MuteMarkerFile;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnProcessExit;
    }

    /// Diktat muted the output and has not restored it yet.
    public bool IsHoldingMute { get { lock (_gate) return _mutedDeviceId is not null; } }

    /// Mutes the default output if it is not muted. Does nothing when already holding a mute.
    public void Mute()
    {
        lock (_gate)
        {
            if (_disposed || _mutedDeviceId is not null) return;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return;
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var volume = device.AudioEndpointVolume;
                if (volume.Mute) return; // already silent: nothing to do, nothing to restore
                WriteMarker(device.ID);   // before muting, so a crash right after is covered
                volume.Mute = true;
                _mutedDeviceId = device.ID;
            }
            catch (Exception ex)
            {
                Log.Warn("Muting the output failed", ex);
                if (_mutedDeviceId is null) DeleteMarker();
            }
        }
    }

    /// Unmutes the endpoint Diktat muted, if it is still muted. Safe to call any time.
    public void Restore()
    {
        lock (_gate)
        {
            var id = _mutedDeviceId;
            _mutedDeviceId = null;
            if (id is null) return;
            Unmute(id);
            DeleteMarker();
        }
    }

    /// Unmutes an endpoint a previous run left muted (crash, kill). Call once at startup.
    public static void RestoreAfterCrash(string? markerFile = null)
    {
        var path = markerFile ?? AppPaths.MuteMarkerFile;
        try
        {
            if (!File.Exists(path)) return;
            var id = File.ReadAllText(path).Trim();
            if (id.Length > 0)
            {
                Log.Info("Restoring an output a previous run left muted");
                Unmute(id);
            }
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn("Restoring the output after a crash failed", ex);
        }
    }

    private static void Unmute(string deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId);
            if (device.State != DeviceState.Active) return; // gone: Windows restores its state when it comes back
            var volume = device.AudioEndpointVolume;
            if (volume.Mute) volume.Mute = false;
        }
        catch (Exception ex)
        {
            Log.Warn("Unmuting the output failed", ex);
        }
    }

    private void WriteMarker(string id)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_markerFile)!);
            File.WriteAllText(_markerFile, id);
        }
        catch (Exception ex)
        {
            Log.Warn("Writing the mute marker failed", ex);
        }
    }

    private void DeleteMarker()
    {
        try { File.Delete(_markerFile); } catch { }
    }

    private void OnProcessExit(object? sender, EventArgs e) => Restore();

    public void Dispose()
    {
        if (_disposed) return;
        Restore();
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException -= OnProcessExit;
    }
}
