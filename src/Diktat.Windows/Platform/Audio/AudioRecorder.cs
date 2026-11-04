using System.Diagnostics;
using NAudio.Wave;

namespace Diktat.Windows.Platform;

/// Records the microphone (WASAPI shared mode) into memory as 16 kHz mono float, the format the
/// speech models read.
///
/// Threading: all public members are thread-safe. Audio arrives on NAudio's capture thread;
/// <see cref="Level"/>, <see cref="NoSignal"/> etc. can be polled from the UI (every ~50 ms).
/// <see cref="Start"/> opens the device (10–200 ms) — call it off the UI thread if that matters,
/// or <see cref="Prewarm"/> ahead of time. <see cref="Interrupted"/> is raised on the event
/// context given to the constructor (or a private background thread).
public sealed class AudioRecorder : IDisposable
{
    public const int SampleRate = CaptureSession.OutputRate;
    /// How much audio from before <see cref="Start"/> a pre-warmed stream contributes.
    public static readonly TimeSpan PrerollLength = TimeSpan.FromMilliseconds(300);

    private readonly EventDispatcher _events;
    private readonly object _sessionGate = new(); // serialises open/close of the device
    private readonly object _dataLock = new();    // guards buffers (capture thread vs. callers)
    private readonly FloatBuffer _buffer = new();
    private readonly List<float> _preroll = new();
    private readonly SignalMeter _meter = new(TimeSpan.FromSeconds(1.5));
    private CaptureSession? _session;
    private string? _sessionRequestedId;
    private System.Threading.Timer? _prewarmTimer;
    private bool _recording;
    private long _firstSampleTimestamp;
    private long _takenSamples;
    private volatile bool _heardOnlySilence;
    private bool _disposed;

    /// The device went away or the stream broke while recording. The samples so far are kept;
    /// call <see cref="Stop"/> to get them.
    public event EventHandler<AudioCaptureException>? Interrupted;

    public AudioRecorder(SynchronizationContext? eventContext = null)
    {
        _events = new EventDispatcher(eventContext);
    }

    public bool IsRecording { get { lock (_dataLock) return _recording; } }

    /// 0...1, adapted to the microphone's loudness (for the bars of the recording pill).
    public float Level => _meter.Level;

    /// The level of the latest 50 ms in dB.
    public float CurrentDecibels => _meter.CurrentDb;

    /// Digital silence for more than 1.5 s while recording: the microphone delivers nothing
    /// (muted in Windows, privacy switch, broken driver). Real rooms never read this low.
    public bool NoSignal => _meter.NoSignal;

    /// After <see cref="Stop"/>: the recording was digital silence from start to end.
    public bool HeardOnlySilence => _heardOnlySilence;

    public TimeSpan Duration
    {
        get { lock (_dataLock) return TimeSpan.FromSeconds((double)(_takenSamples + _buffer.Count) / SampleRate); }
    }

    /// <see cref="Stopwatch"/> timestamp of the first recorded sample (approximate, ±10 ms), for
    /// aligning tracks. 0 before the first sample.
    public long FirstSampleTimestamp { get { lock (_dataLock) return _firstSampleTimestamp; } }

    /// The endpoint that records (or recorded last).
    public string? DeviceId { get; private set; }
    public string? DeviceName { get; private set; }
    /// The chosen microphone was missing, so the default input records.
    public bool UsedFallbackDevice { get; private set; }
    /// The device's own format before conversion.
    public WaveFormat? NativeFormat { get; private set; }

    /// Opens the microphone ahead of <see cref="Start"/> (e.g. on
    /// <see cref="GlobalHotkeyService.PotentialStart"/>), so recording begins without the device
    /// start-up delay and includes up to <see cref="PrerollLength"/> of audio from before Start.
    /// Closes again after <paramref name="keepOpen"/> if Start does not come. Runs in the
    /// background; errors are only logged (Start reports them).
    public void Prewarm(string? deviceId, TimeSpan keepOpen)
    {
        if (_disposed) return;
        Task.Run(() =>
        {
            lock (_sessionGate)
            {
                if (_disposed || IsRecording) return;
                ArmPrewarmTimer(keepOpen);
                if (_session is { IsAlive: true } && _sessionRequestedId == deviceId) return;
                CloseSession();
                try
                {
                    OpenSession(deviceId);
                }
                catch (Exception ex)
                {
                    Log.Warn("Microphone pre-warm failed", ex);
                }
            }
        });
    }

    /// Starts recording <paramref name="deviceId"/> (endpoint ID), or the default input when
    /// null or not connected. Throws <see cref="AudioCaptureException"/> with a German message.
    public void Start(string? deviceId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sessionGate)
        {
            if (IsRecording) return;
            _prewarmTimer?.Dispose();
            _prewarmTimer = null;
            _heardOnlySilence = false;

            bool reuse = _session is { IsAlive: true } && _sessionRequestedId == deviceId;
            if (!reuse)
            {
                CloseSession();
                OpenSession(deviceId);
            }

            lock (_dataLock)
            {
                _buffer.Clear();
                _takenSamples = 0;
                _meter.Reset();
                if (reuse && _preroll.Count > 0)
                {
                    _buffer.Add(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_preroll));
                    _firstSampleTimestamp = Stopwatch.GetTimestamp() - (long)((double)_preroll.Count / SampleRate * Stopwatch.Frequency);
                }
                else
                {
                    _firstSampleTimestamp = 0;
                }
                _preroll.Clear();
                _recording = true;
            }
        }
    }

    /// Stops and returns the recording as 16 kHz mono float. Empty when nothing was recorded.
    public float[] Stop() => Finish(keep: true);

    /// Stops and throws the audio away.
    public void Discard() => Finish(keep: false);

    /// A copy of the samples recorded so far, from <paramref name="startIndex"/> (for live
    /// previews or streaming transcription).
    public float[] GetSamples(long startIndex = 0)
    {
        lock (_dataLock) return _buffer.ToArray(startIndex);
    }

    /// Removes and returns the samples recorded since the last call, so long recordings (meetings)
    /// can be written to disk in chunks instead of growing in memory. <see cref="Stop"/> then
    /// returns only the rest; <see cref="Duration"/> keeps counting everything.
    public float[] TakeSamples()
    {
        lock (_dataLock)
        {
            var samples = _buffer.ToArray();
            _takenSamples += samples.Length;
            _buffer.Clear();
            return samples;
        }
    }

    private float[] Finish(bool keep)
    {
        lock (_sessionGate)
        {
            _session?.Stop(); // flushes the resampler tail into the buffer
            CloseSession();
            lock (_dataLock)
            {
                bool wasRecording = _recording;
                _recording = false;
                var samples = keep && wasRecording ? _buffer.ToArray() : [];
                double seconds = (double)(_takenSamples + _buffer.Count) / SampleRate;
                _heardOnlySilence = wasRecording && seconds > 0.6 && _meter.PeakDb < SignalMeter.SilenceDb;
                _buffer.Clear();
                _preroll.Clear();
                _meter.Level = 0;
                _meter.NoSignal = false;
                return samples;
            }
        }
    }

    private void OpenSession(string? deviceId)
    {
        var session = CaptureSession.StartMicrophone(deviceId, OnSamples);
        session.Faulted += OnFaulted;
        _session = session;
        _sessionRequestedId = deviceId;
        DeviceId = session.DeviceId;
        DeviceName = session.DeviceName;
        UsedFallbackDevice = session.UsedFallbackDevice;
        NativeFormat = session.NativeFormat;
        Log.Info($"Microphone open: {session.NativeFormat.SampleRate} Hz, {session.NativeFormat.Channels} ch, {session.NativeFormat.BitsPerSample} bit{(session.UsedFallbackDevice ? " (fallback to default)" : "")}");
    }

    private void CloseSession()
    {
        var session = _session;
        _session = null;
        _sessionRequestedId = null;
        if (session is null) return;
        session.Faulted -= OnFaulted;
        session.Dispose();
    }

    private void ArmPrewarmTimer(TimeSpan keepOpen)
    {
        _prewarmTimer?.Dispose();
        _prewarmTimer = new System.Threading.Timer(_ =>
        {
            lock (_sessionGate)
            {
                if (IsRecording) return;
                CloseSession();
                lock (_dataLock) _preroll.Clear();
            }
        }, null, keepOpen, Timeout.InfiniteTimeSpan);
    }

    // Capture thread.
    private void OnSamples(List<float> samples, long arrival)
    {
        lock (_dataLock)
        {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples);
            if (_recording)
            {
                if (_firstSampleTimestamp == 0)
                {
                    _firstSampleTimestamp = arrival - (long)((double)samples.Count / SampleRate * Stopwatch.Frequency);
                }
                _buffer.Add(span);
                _meter.Add(span);
            }
            else
            {
                _preroll.AddRange(samples);
                int max = (int)(PrerollLength.TotalSeconds * SampleRate);
                if (_preroll.Count > max) _preroll.RemoveRange(0, _preroll.Count - max);
            }
        }
    }

    // Capture thread.
    private void OnFaulted(Exception error)
    {
        bool recording = IsRecording;
        if (!recording) return; // a pre-warmed stream that broke is simply reopened by Start
        var failure = error as AudioCaptureException
            ?? new AudioCaptureException(AudioCaptureFailure.DeviceLost, "Das Mikrofon wurde während der Aufnahme getrennt.", error);
        _events.Post(() => Interrupted?.Invoke(this, failure));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_sessionGate)
        {
            _prewarmTimer?.Dispose();
            _prewarmTimer = null;
            CloseSession();
            lock (_dataLock)
            {
                _recording = false;
                _buffer.Clear();
            }
        }
        _events.Dispose();
    }
}
