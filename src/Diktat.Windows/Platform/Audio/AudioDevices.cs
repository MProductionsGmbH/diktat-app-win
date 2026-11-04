using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Diktat.Windows.Platform;

/// An audio endpoint. <see cref="Id"/> is the endpoint ID string, stable across reconnects of
/// the same device; that is what the settings store (AppSettings.MicrophoneId).
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// Which microphone a recording uses (port of the Mac MicrophoneSelection).
public abstract record MicrophoneResolution
{
    /// Nothing picked: the recording follows the Windows default input.
    public sealed record SystemDefault : MicrophoneResolution;
    public sealed record Device(AudioDeviceInfo Info) : MicrophoneResolution;
    /// A device was picked but is not connected now; the recording uses the default input.
    public sealed record Missing(string Id) : MicrophoneResolution;
}

public enum AudioCaptureFailure
{
    /// Windows privacy settings block microphone access for desktop apps.
    AccessDenied,
    /// No input device (or the chosen one is gone and there is no default).
    NoDevice,
    /// Another app holds the device exclusively.
    DeviceInUse,
    /// The device was unplugged or disabled while recording.
    DeviceLost,
    Other,
}

/// A recording could not start or broke off. <see cref="Exception.Message"/> is German and
/// meant for the user.
public sealed class AudioCaptureException : Exception
{
    public AudioCaptureFailure Failure { get; }

    public AudioCaptureException(AudioCaptureFailure failure, string message, Exception? inner = null)
        : base(message, inner) => Failure = failure;

    internal static AudioCaptureException From(Exception ex)
    {
        if (ex is AudioCaptureException ace) return ace;
        int hr = ex is COMException com ? com.HResult : ex.HResult;
        return (uint)hr switch
        {
            0x80070005 => new AudioCaptureException(AudioCaptureFailure.AccessDenied,
                "Windows erlaubt Diktat den Zugriff auf das Mikrofon nicht. Bitte unter Einstellungen › Datenschutz › Mikrofon den Zugriff für Desktop-Apps erlauben.", ex),
            0x80070490 => new AudioCaptureException(AudioCaptureFailure.NoDevice,
                "Kein Mikrofon gefunden.", ex),
            0x88890004 => new AudioCaptureException(AudioCaptureFailure.DeviceLost,
                "Das Mikrofon wurde getrennt.", ex),
            0x8889000A => new AudioCaptureException(AudioCaptureFailure.DeviceInUse,
                "Das Mikrofon wird gerade exklusiv von einer anderen App verwendet.", ex),
            _ => new AudioCaptureException(AudioCaptureFailure.Other,
                $"Die Aufnahme konnte nicht gestartet werden ({ex.Message}).", ex),
        };
    }
}

/// Lists audio endpoints. All methods may be called from any thread (Core Audio is free-threaded).
public static class AudioDevices
{
    public static IReadOnlyList<AudioDeviceInfo> InputDevices() => List(DataFlow.Capture);

    public static IReadOnlyList<AudioDeviceInfo> OutputDevices() => List(DataFlow.Render);

    public static string? DefaultInputId() => DefaultId(DataFlow.Capture);

    public static string? DefaultOutputId() => DefaultId(DataFlow.Render);

    public static MicrophoneResolution Resolve(string? preferredId)
    {
        if (string.IsNullOrEmpty(preferredId)) return new MicrophoneResolution.SystemDefault();
        var device = InputDevices().FirstOrDefault(d => string.Equals(d.Id, preferredId, StringComparison.OrdinalIgnoreCase));
        return device is not null ? new MicrophoneResolution.Device(device) : new MicrophoneResolution.Missing(preferredId);
    }

    /// The Windows input volume (0...1) of the device, or null when unknown. Only read, never
    /// changed: the input volume belongs to the user. Below ~0.25 words get lost.
    public static float? InputVolume(string? deviceId = null)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = OpenCapture(enumerator, deviceId, out _);
            return device.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
        catch
        {
            return null;
        }
    }

    /// Opens the capture endpoint <paramref name="deviceId"/> if it is present and active,
    /// otherwise the default input. Throws <see cref="AudioCaptureException"/> when there is none.
    internal static MMDevice OpenCapture(MMDeviceEnumerator enumerator, string? deviceId, out bool usedFallback)
    {
        usedFallback = false;
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                var device = enumerator.GetDevice(deviceId);
                if (device.State == DeviceState.Active) return device;
                device.Dispose();
            }
            catch (COMException)
            {
                // Not present: fall back to the default.
            }
            usedFallback = true;
        }
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
            throw new AudioCaptureException(AudioCaptureFailure.NoDevice, "Kein Mikrofon gefunden.");
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
    }

    internal static MMDevice OpenDefaultRender(MMDeviceEnumerator enumerator)
    {
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            throw new AudioCaptureException(AudioCaptureFailure.NoDevice, "Kein Wiedergabegerät gefunden.");
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private static List<AudioDeviceInfo> List(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = DefaultId(enumerator, flow);
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    string name;
                    try { name = device.FriendlyName; } catch { name = device.ID; }
                    result.Add(new AudioDeviceInfo(device.ID, name, string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Listing {flow} devices failed", ex);
        }
        return result;
    }

    private static string? DefaultId(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return DefaultId(enumerator, flow);
        }
        catch
        {
            return null;
        }
    }

    private static string? DefaultId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var role = flow == DataFlow.Capture ? Role.Console : Role.Multimedia;
        if (!enumerator.HasDefaultAudioEndpoint(flow, role)) return null;
        using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
        return device.ID;
    }

    /// Runs <paramref name="create"/> without a SynchronizationContext. NAudio's capture classes
    /// capture SynchronizationContext.Current in their constructor and post RecordingStopped to
    /// it; created on the UI thread, a Stop() that waits for that event would deadlock.
    internal static T WithoutSyncContext<T>(Func<T> create)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return create();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}

/// Reports device changes (plugged in, removed, default changed) through IMMNotificationClient.
/// Events are raised on the context given to the constructor (or a private background thread),
/// never on the Core Audio notification thread.
public sealed class AudioDeviceWatcher : IDisposable
{
    private readonly EventDispatcher _events;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly Client _client;
    private bool _registered;

    /// The set of devices or their state changed (refresh the microphone list).
    public event EventHandler? DevicesChanged;
    /// The default input (Console role) changed; the argument is the new endpoint ID or null.
    public event EventHandler<string?>? DefaultInputChanged;
    /// The default output (Multimedia role) changed; the argument is the new endpoint ID or null.
    public event EventHandler<string?>? DefaultOutputChanged;

    public AudioDeviceWatcher(SynchronizationContext? eventContext = null)
    {
        _events = new EventDispatcher(eventContext);
        _enumerator = new MMDeviceEnumerator();
        _client = new Client(this);
        _registered = _enumerator.RegisterEndpointNotificationCallback(_client) == 0;
        if (!_registered) Log.Warn("Audio device notifications could not be registered");
    }

    [ComVisible(true)]
    private sealed class Client(AudioDeviceWatcher owner) : IMMNotificationClient
    {
        // Called on a Core Audio thread: only post, never block or call back into Core Audio here.
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.Post(() => owner.DevicesChanged?.Invoke(owner, EventArgs.Empty));
        public void OnDeviceAdded(string pwstrDeviceId) => owner.Post(() => owner.DevicesChanged?.Invoke(owner, EventArgs.Empty));
        public void OnDeviceRemoved(string deviceId) => owner.Post(() => owner.DevicesChanged?.Invoke(owner, EventArgs.Empty));

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Capture && role == Role.Console)
            {
                owner.Post(() =>
                {
                    owner.DefaultInputChanged?.Invoke(owner, defaultDeviceId);
                    owner.DevicesChanged?.Invoke(owner, EventArgs.Empty);
                });
            }
            else if (flow == DataFlow.Render && role == Role.Multimedia)
            {
                owner.Post(() => owner.DefaultOutputChanged?.Invoke(owner, defaultDeviceId));
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Friendly names change rarely; ignored to avoid floods of notifications.
        }
    }

    private void Post(Action action) => _events.Post(action);

    public void Dispose()
    {
        if (_registered)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_client); } catch { }
            _registered = false;
        }
        _enumerator.Dispose();
        _events.Dispose();
    }
}
