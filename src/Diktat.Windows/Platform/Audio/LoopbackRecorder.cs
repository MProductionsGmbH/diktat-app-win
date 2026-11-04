using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Diktat.Windows.Platform;

/// Records what the PC plays (the other side of an online meeting) through WASAPI loopback of
/// the default output, as 16 kHz mono float on a timeline that starts at <see cref="Start"/>.
///
/// WASAPI loopback delivers no packets while nothing plays. Two measures keep the track in step
/// with the wall clock (and so with the microphone): a silent render stream on the same device
/// keeps the engine running, and gaps longer than 100 ms are filled with silence by the clock.
/// When the default output changes (headset plugged in) the capture moves to the new device.
///
/// Threading: public members are thread-safe; <see cref="OutputChanged"/> is raised on the
/// event context (or a private background thread).
public sealed class LoopbackRecorder : IDisposable
{
    public const int SampleRate = CaptureSession.OutputRate;
    private const int GapThresholdSamples = SampleRate / 10;

    private readonly EventDispatcher _events;
    private readonly object _gate = new();
    private readonly object _dataLock = new();
    private readonly FloatBuffer _buffer = new();
    private readonly SignalMeter _meter = new(TimeSpan.FromSeconds(3));
    private AudioDeviceWatcher? _watcher;
    private CaptureSession? _session;
    private WasapiOut? _keepAlive;
    private MMDevice? _keepAliveDevice;
    private bool _recording;
    private long _startTimestamp;
    private long _takenSamples;
    private float _peak;
    private bool _disposed;

    /// The capture moved to another output device: null when that worked, else the error
    /// (recording continues with silence and retries on the next device change).
    public event EventHandler<Exception?>? OutputChanged;

    public LoopbackRecorder(SynchronizationContext? eventContext = null)
    {
        _events = new EventDispatcher(eventContext);
    }

    /// Play silence on the output while recording, so loopback keeps delivering. Default on.
    public bool KeepAliveWithSilence { get; set; } = true;

    public bool IsRecording { get { lock (_dataLock) return _recording; } }
    public float Level => _meter.Level;
    /// Loudest sample so far, 0...1: 0 means nothing played (or nothing arrived).
    public float Peak { get { lock (_dataLock) return _peak; } }
    public TimeSpan Duration { get { lock (_dataLock) return TimeSpan.FromSeconds((double)(_takenSamples + _buffer.Count) / SampleRate); } }
    /// <see cref="Stopwatch"/> timestamp the track's sample 0 corresponds to.
    public long FirstSampleTimestamp { get { lock (_dataLock) return _startTimestamp; } }
    public string? DeviceName { get; private set; }

    /// Starts recording the default output. Throws <see cref="AudioCaptureException"/>.
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (IsRecording) return;
            lock (_dataLock)
            {
                _buffer.Clear();
                _takenSamples = 0;
                _peak = 0;
                _meter.Reset();
                _startTimestamp = Stopwatch.GetTimestamp();
                _recording = true;
            }
            try
            {
                OpenSession();
            }
            catch
            {
                lock (_dataLock) _recording = false;
                throw;
            }
            _watcher = new AudioDeviceWatcher();
            _watcher.DefaultOutputChanged += OnDefaultOutputChanged;
        }
    }

    public float[] Stop() => Finish(keep: true);

    public void Discard() => Finish(keep: false);

    /// Removes and returns the samples since the last call (for writing long meetings to disk).
    public float[] TakeSamples()
    {
        lock (_dataLock)
        {
            FillGapUntil(Stopwatch.GetTimestamp());
            var samples = _buffer.ToArray();
            _takenSamples += samples.Length;
            _buffer.Clear();
            return samples;
        }
    }

    private float[] Finish(bool keep)
    {
        lock (_gate)
        {
            if (_watcher is not null)
            {
                _watcher.DefaultOutputChanged -= OnDefaultOutputChanged;
                _watcher.Dispose();
                _watcher = null;
            }
            _session?.Stop();
            CloseSession();
            lock (_dataLock)
            {
                if (!_recording) return [];
                FillGapUntil(Stopwatch.GetTimestamp());
                _recording = false;
                var samples = keep ? _buffer.ToArray() : [];
                _buffer.Clear();
                _meter.Level = 0;
                return samples;
            }
        }
    }

    private void OpenSession()
    {
        var session = CaptureSession.StartLoopback(OnSamples);
        session.Faulted += OnFaulted;
        _session = session;
        DeviceName = session.DeviceName;
        if (KeepAliveWithSilence) StartKeepAlive(session.DeviceId);
        Log.Info($"Loopback open: {session.NativeFormat.SampleRate} Hz, {session.NativeFormat.Channels} ch");
    }

    private void CloseSession()
    {
        StopKeepAlive();
        var session = _session;
        _session = null;
        if (session is null) return;
        session.Faulted -= OnFaulted;
        session.Dispose();
    }

    private void StartKeepAlive(string deviceId)
    {
        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(deviceId);
            WaveFormat format;
            using (var client = device.AudioClient) format = client.MixFormat;
            var output = AudioDevices.WithoutSyncContext(() => new WasapiOut(device, AudioClientShareMode.Shared, true, 100));
            output.Init(new SilenceProvider(format));
            output.Play();
            _keepAlive = output;
            _keepAliveDevice = device;
        }
        catch (Exception ex)
        {
            device?.Dispose();
            // Without it, gaps are filled by the clock; nothing is lost.
            Log.Warn("Silent keep-alive stream failed", ex);
        }
    }

    private void StopKeepAlive()
    {
        var output = _keepAlive;
        var device = _keepAliveDevice;
        _keepAlive = null;
        _keepAliveDevice = null;
        if (output is not null)
        {
            try { output.Stop(); } catch { }
            try { output.Dispose(); } catch { }
        }
        try { device?.Dispose(); } catch { }
    }

    private void OnDefaultOutputChanged(object? sender, string? deviceId) => Task.Run(() => Restart("default output changed"));

    private void OnFaulted(Exception error) => Task.Run(async () =>
    {
        // The device may come back as default a moment later; give Windows time to settle.
        await Task.Delay(500).ConfigureAwait(false);
        Restart("capture failed");
    });

    private void Restart(string reason)
    {
        Exception? failure = null;
        lock (_gate)
        {
            if (!IsRecording || _disposed) return;
            _session?.Stop();
            CloseSession();
            try
            {
                OpenSession();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }
        Log.Info($"Loopback restarted ({reason}){(failure is null ? "" : ": " + failure.Message)}");
        _events.Post(() => OutputChanged?.Invoke(this, failure));
    }

    // Capture thread.
    private void OnSamples(List<float> samples, long arrival)
    {
        lock (_dataLock)
        {
            if (!_recording) return;
            long arrivalStart = arrival - (long)((double)samples.Count / SampleRate * Stopwatch.Frequency);
            FillGapUntil(arrivalStart);
            var span = CollectionsMarshal.AsSpan(samples);
            _buffer.Add(span);
            _meter.Add(span);
            foreach (var sample in span)
            {
                var magnitude = Math.Abs(sample);
                if (magnitude > _peak) _peak = magnitude;
            }
        }
    }

    /// Pads with silence when the track lags the clock by more than 100 ms. Caller holds _dataLock.
    private void FillGapUntil(long timestamp)
    {
        long expected = (long)((double)(timestamp - _startTimestamp) / Stopwatch.Frequency * SampleRate);
        long have = _takenSamples + _buffer.Count;
        if (expected - have > GapThresholdSamples) _buffer.AddSilence(expected - have);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Finish(keep: false);
        _disposed = true;
        _events.Dispose();
    }
}

/// Microphone and system audio side by side, for meetings. Both tracks are 16 kHz mono and
/// aligned: sample i of each was heard at the same moment (± ~20 ms).
public sealed record MeetingTracks(float[] Microphone, float[] SystemAudio, int SampleRate)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Math.Max(Microphone.Length, SystemAudio.Length) / SampleRate);
}

/// Records a meeting: the microphone and what the PC plays, with a shared start, so the two
/// tracks can be transcribed separately ("Ich" / "Andere") and merged by time.
/// Do not mute the output (<see cref="SystemAudioMuter"/>) while this records.
public sealed class MeetingRecorder : IDisposable
{
    public AudioRecorder Microphone { get; }
    public LoopbackRecorder SystemAudio { get; }

    public MeetingRecorder(SynchronizationContext? eventContext = null)
    {
        Microphone = new AudioRecorder(eventContext);
        SystemAudio = new LoopbackRecorder(eventContext);
    }

    public bool IsRecording => Microphone.IsRecording || SystemAudio.IsRecording;

    /// Starts both. If the system audio cannot be recorded the microphone still records and
    /// the exception is returned (null when both run). Throws when the microphone fails.
    public Exception? Start(string? microphoneId)
    {
        Exception? loopbackError = null;
        try
        {
            SystemAudio.Start();
        }
        catch (Exception ex)
        {
            loopbackError = ex;
            Log.Warn("Meeting: system audio not available", ex);
        }
        try
        {
            Microphone.Start(microphoneId);
        }
        catch
        {
            SystemAudio.Discard();
            throw;
        }
        return loopbackError;
    }

    /// Stops both and aligns them: the later track is padded at the front, the shorter at the end.
    public MeetingTracks Stop()
    {
        long micStart = Microphone.FirstSampleTimestamp;
        long systemStart = SystemAudio.FirstSampleTimestamp;
        bool hadSystem = SystemAudio.IsRecording;
        var mic = Microphone.Stop();
        var system = SystemAudio.Stop();
        if (!hadSystem || systemStart == 0) systemStart = micStart;
        if (micStart == 0) micStart = systemStart;
        return Align(mic, micStart, system, systemStart, AudioRecorder.SampleRate);
    }

    public void Discard()
    {
        Microphone.Discard();
        SystemAudio.Discard();
    }

    internal static MeetingTracks Align(float[] a, long aStart, float[] b, long bStart, int rate)
    {
        long origin = Math.Min(aStart, bStart);
        int aPad = (int)Math.Round((double)(aStart - origin) / Stopwatch.Frequency * rate);
        int bPad = (int)Math.Round((double)(bStart - origin) / Stopwatch.Frequency * rate);
        int length = Math.Max(aPad + a.Length, bPad + b.Length);
        var alignedA = new float[length];
        var alignedB = new float[length];
        a.CopyTo(alignedA, aPad);
        b.CopyTo(alignedB, bPad);
        return new MeetingTracks(alignedA, alignedB, rate);
    }

    public void Dispose()
    {
        Microphone.Dispose();
        SystemAudio.Dispose();
    }
}
