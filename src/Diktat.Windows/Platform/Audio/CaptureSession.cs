using System.IO;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Diktat.Windows.Platform;

/// One open WASAPI stream (microphone or loopback), converted to 16 kHz mono float.
/// The sample callback runs on NAudio's capture thread.
internal sealed class CaptureSession : IDisposable
{
    public const int OutputRate = 16_000;

    /// <paramref name="samples"/> is only valid during the call. <paramref name="arrivalTimestamp"/>
    /// is the <see cref="Stopwatch"/> timestamp when the packet arrived.
    public delegate void SamplesHandler(List<float> samples, long arrivalTimestamp);

    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly PcmToMono _converter;
    private readonly StreamingResampler _resampler;
    private readonly List<float> _scratch = new(4096);
    private readonly ManualResetEventSlim _stopped = new();
    private readonly object _lock = new();
    private readonly SamplesHandler _onSamples;
    private volatile bool _stopRequested;
    private bool _disposed;

    public string DeviceId { get; }
    public string DeviceName { get; }
    public WaveFormat NativeFormat { get; }
    public bool UsedFallbackDevice { get; init; }
    public bool IsAlive => !_stopped.IsSet;
    /// Raised (on the capture thread) when the stream ended without being asked to.
    public event Action<Exception>? Faulted;

    private CaptureSession(MMDevice device, bool loopback, SamplesHandler onSamples)
    {
        _device = device;
        _onSamples = onSamples;
        DeviceId = device.ID;
        try { DeviceName = device.FriendlyName; } catch { DeviceName = device.ID; }

        _capture = AudioDevices.WithoutSyncContext<WasapiCapture>(() => loopback
            ? new WasapiLoopbackCapture(device)
            : new WasapiCapture(device, true, 50));
        _capture.ShareMode = AudioClientShareMode.Shared;
        NativeFormat = _capture.WaveFormat;
        _converter = new PcmToMono(NativeFormat);
        _resampler = new StreamingResampler(NativeFormat.SampleRate, OutputRate);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    /// Opens and starts the microphone. Throws <see cref="AudioCaptureException"/>.
    public static CaptureSession StartMicrophone(string? deviceId, SamplesHandler onSamples)
    {
        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = AudioDevices.OpenCapture(enumerator, deviceId, out var fallback);
            var session = new CaptureSession(device, loopback: false, onSamples) { UsedFallbackDevice = fallback };
            session.StartCapture();
            return session;
        }
        catch (Exception ex)
        {
            device?.Dispose();
            throw AudioCaptureException.From(ex);
        }
    }

    /// Opens and starts loopback capture of the default output. Throws <see cref="AudioCaptureException"/>.
    public static CaptureSession StartLoopback(SamplesHandler onSamples)
    {
        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = AudioDevices.OpenDefaultRender(enumerator);
            var session = new CaptureSession(device, loopback: true, onSamples);
            session.StartCapture();
            return session;
        }
        catch (Exception ex)
        {
            device?.Dispose();
            throw AudioCaptureException.From(ex);
        }
    }

    private void StartCapture()
    {
        try
        {
            _capture.StartRecording();
        }
        catch
        {
            _capture.Dispose();
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                var mono = _converter.Convert(e.Buffer.AsSpan(0, e.BytesRecorded));
                _scratch.Clear();
                _resampler.Process(mono, _scratch);
                if (_scratch.Count > 0) _onSamples(_scratch, now);
            }
            catch (Exception ex)
            {
                Log.Error("Audio conversion failed", ex);
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _stopped.Set();
        if (!_stopRequested)
        {
            var error = e.Exception ?? new AudioCaptureException(AudioCaptureFailure.DeviceLost, "Die Audioaufnahme wurde unterbrochen.");
            Log.Warn($"Capture on '{DeviceName}' ended unexpectedly", error);
            Faulted?.Invoke(error);
        }
    }

    /// Stops the stream, waits for the capture thread to finish (≤ 1 s) and emits the
    /// resampler's tail through the sample callback.
    public void Stop()
    {
        if (!_stopRequested)
        {
            _stopRequested = true;
            if (!_stopped.IsSet)
            {
                try { _capture.StopRecording(); } catch (Exception ex) { Log.Warn("StopRecording failed", ex); }
                if (!_stopped.Wait(TimeSpan.FromSeconds(1))) Log.Warn("Capture thread did not stop within 1 s");
            }
        }
        lock (_lock)
        {
            if (_disposed) return;
            _scratch.Clear();
            _resampler.Flush(_scratch);
            if (_scratch.Count > 0) _onSamples(_scratch, Stopwatch.GetTimestamp());
            _scratch.Clear();
        }
    }

    public void Dispose()
    {
        if (!_stopRequested) Stop();
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        try { _capture.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
        // _stopped is not disposed: a capture thread that did not stop in time may still set it.
    }
}
