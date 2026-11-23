using System.Windows.Threading;
using Diktat.Windows.Platform;

namespace Diktat.Windows.Runtime;

/// "Ton testen": listens to what the PC plays, writes nothing down, and shows a level, so you
/// see before the meeting that the sound of the call arrives (SystemSoundTest.swift).
/// UI thread only.
public sealed class SystemSoundTest : ObservableObject, IDisposable
{
    /// A test ends by itself, so a forgotten one does not keep listening.
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);
    public const float HearingThreshold = 0.05f;
    private static readonly TimeSpan HearingHold = TimeSpan.FromSeconds(1.5);

    private readonly DispatcherTimer _timer;
    private LoopbackRecorder? _recorder;
    private DateTime _startedAt;
    private DateTime _lastSound = DateTime.MinValue;
    private bool _isRunning;
    private bool _isStarting;
    private bool _stopWhileStarting;
    private float _level;
    private bool _isHearingSound;
    private string? _problem;

    public SystemSoundTest()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
    }

    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }
    /// 0...1, like the microphone's level.
    public float Level { get => _level; private set => SetProperty(ref _level, value); }
    /// The PC played something in the last moments.
    public bool IsHearingSound { get => _isHearingSound; private set => SetProperty(ref _isHearingSound, value); }
    /// Why the test could not start, or null.
    public string? Problem { get => _problem; private set => SetProperty(ref _problem, value); }

    public async void Start()
    {
        if (IsRunning || _isStarting) return;
        _isStarting = true;
        _stopWhileStarting = false;
        Problem = null;
        var recorder = new LoopbackRecorder();
        try
        {
            await Task.Run(recorder.Start);
        }
        catch (Exception ex)
        {
            await Task.Run(recorder.Dispose);
            Log.Warn("System sound test could not start", ex);
            Problem = ex.Message;
            return;
        }
        finally
        {
            _isStarting = false;
        }
        if (_stopWhileStarting)
        {
            _ = Task.Run(recorder.Dispose);
            return;
        }
        _recorder = recorder;
        _startedAt = DateTime.UtcNow;
        _lastSound = DateTime.MinValue;
        IsRunning = true;
        _timer.Start();
    }

    public void Stop()
    {
        _stopWhileStarting = _isStarting;
        _timer.Stop();
        var recorder = _recorder;
        _recorder = null;
        if (recorder is not null) _ = Task.Run(recorder.Dispose);
        IsRunning = false;
        Level = 0;
        IsHearingSound = false;
    }

    private void Tick()
    {
        if (_recorder is not { } recorder) return;
        // Nothing is kept: the samples only make the level.
        recorder.TakeSamples();
        Level = recorder.Level;
        var now = DateTime.UtcNow;
        if (Level > HearingThreshold) _lastSound = now;
        IsHearingSound = now - _lastSound < HearingHold;
        if (now - _startedAt > Duration) Stop();
    }

    public void Dispose() => Stop();
}
