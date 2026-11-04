using System.ComponentModel;
using System.Windows.Threading;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;

namespace Diktat.Windows.Pill;

/// Shows the recording pill while Diktat records or writes, so the recording is visible wherever
/// you type (RecordingIndicatorController.swift). A click on the pill stops a recording; during
/// a meeting it shows the running time and a click opens the meeting page.
public sealed class PillController : IDisposable
{
    private static readonly TimeSpan HintDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DoneVisible = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan CopiedVisible = TimeSpan.FromMilliseconds(2600);
    private static readonly TimeSpan ErrorVisible = TimeSpan.FromMilliseconds(1600);

    private readonly DiktatApp _app;
    private readonly RecordingPillWindow _window = new();
    private readonly DispatcherTimer _meter;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _hintTimer;
    private PillState _state = PillState.Hidden;
    private bool _showsMicrophone;
    private string? _lastAnnouncedMicrophone;

    public PillController(DiktatApp app)
    {
        _app = app;
        _meter = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(70) };
        _meter.Tick += (_, _) => Meter();
        _hideTimer = new DispatcherTimer();
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
        _hintTimer = new DispatcherTimer { Interval = HintDuration };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            _showsMicrophone = false;
            UpdateHint();
        };
        _window.PillClicked += OnPillClicked;
        _app.PropertyChanged += OnAppChanged;
        _app.Dictation.PropertyChanged += OnDictationChanged;
        _app.MeetingChanged += OnMeetingChanged;
    }

    /// The window, for the smoke test.
    internal RecordingPillWindow Window => _window;

    /// "Aufnahme-Anzeige" or its position changed: apply at once, also mid-recording.
    public void SettingsChanged()
    {
        if (!_app.Services.Settings.App.ShowsRecordingIndicator)
        {
            Hide();
            _state = PillState.Hidden;
            return;
        }
        Update(replace: true);
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiktatApp.Phase) or nameof(DiktatApp.LastTextWasOnlyCopied)) Update(replace: false);
    }

    private void OnDictationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DictationController.MicrophoneName) && _state == PillState.Recording) AnnounceMicrophone();
        if (e.PropertyName == nameof(DictationController.NoSignal)) UpdateHint();
    }

    private void OnMeetingChanged(object? sender, EventArgs e)
    {
        if (_state == PillState.Meeting) _window.Visual.MeetingElapsed = _app.CurrentMeeting?.Elapsed ?? TimeSpan.Zero;
        // A meeting that starts or ends matters only while no dictation shows.
        if (_app.Phase == DictationPhase.Idle) Update(replace: false);
    }

    private void Update(bool replace)
    {
        var meeting = _app.CurrentMeeting;
        var state = PillLayout.StateFor(_app.Phase, _app.LastTextWasOnlyCopied, meeting?.Phase == MeetingPhase.Recording,
            _app.Services.Settings.App.ShowsRecordingIndicator);
        if (state == _state && !replace) return;
        var previous = _state;
        _state = state;
        _hideTimer.Stop();
        try
        {
            Apply(state, previous, replace);
        }
        catch (Exception ex)
        {
            Log.Error("Updating the recording pill failed", ex);
        }
    }

    private void Apply(PillState state, PillState previous, bool replace)
    {
        var position = _app.Services.Settings.App.PillPosition;
        switch (state)
        {
            case PillState.Recording:
                if (previous != PillState.Recording)
                {
                    _window.Visual.ResetLevels();
                    _showsMicrophone = false;
                    AnnounceMicrophone();
                }
                _meter.Start();
                _window.SetState(state, clickable: true);
                UpdateHint();
                // A new recording is placed anew, e.g. after the taskbar moved.
                _window.ShowPill(place: previous != PillState.Recording || replace, position);
                break;
            case PillState.Processing:
                StopMeter();
                _window.SetState(state, clickable: false);
                UpdateHint();
                _window.ShowPill(place: replace, position);
                break;
            case PillState.Done or PillState.Copied or PillState.Error:
                StopMeter();
                // A result only shows on a pill that is up already (a dictation just ended).
                if (!_window.IsVisible || previous == PillState.Hidden)
                {
                    Hide();
                    return;
                }
                _window.SetState(state, clickable: false);
                UpdateHint();
                _window.ShowPill(place: replace, position);
                _hideTimer.Interval = state switch
                {
                    PillState.Done => DoneVisible,
                    PillState.Copied => CopiedVisible,
                    _ => ErrorVisible,
                };
                _hideTimer.Start();
                break;
            case PillState.Meeting:
                StopMeter();
                _window.Visual.MeetingElapsed = _app.CurrentMeeting?.Elapsed ?? TimeSpan.Zero;
                _window.SetState(state, clickable: true);
                UpdateHint();
                _window.ShowPill(place: previous is PillState.Hidden || replace, position);
                break;
            default:
                Hide();
                break;
        }
    }

    /// The label names the microphone for a few seconds, only when it differs from last time.
    private void AnnounceMicrophone()
    {
        var name = _app.Dictation.MicrophoneName;
        if (string.IsNullOrEmpty(name) || name == _lastAnnouncedMicrophone) return;
        _lastAnnouncedMicrophone = name;
        _showsMicrophone = true;
        _hintTimer.Stop();
        _hintTimer.Start();
        UpdateHint();
    }

    private void UpdateHint()
    {
        var visual = _window.Visual;
        bool noSignal = _state == PillState.Recording && _app.NoSignal;
        visual.Silent = noSignal;
        visual.HintIsWarning = noSignal;
        visual.Hint = PillLayout.HintText(_state, _app.Dictation.MicrophoneName, noSignal, _showsMicrophone);
        visual.InvalidateVisual();
    }

    private void Meter()
    {
        if (_state != PillState.Recording) return;
        _window.Visual.PushLevel(_app.AudioLevel);
    }

    private void StopMeter()
    {
        _meter.Stop();
        _hintTimer.Stop();
        _showsMicrophone = false;
    }

    private void Hide()
    {
        StopMeter();
        _window.HidePill();
        // After a dictation, a meeting that still records comes back.
        if (_state is PillState.Done or PillState.Copied or PillState.Error)
        {
            _state = PillState.Hidden;
            if (_app.CurrentMeeting?.Phase == MeetingPhase.Recording && _app.Phase is DictationPhase.Done or DictationPhase.Error or DictationPhase.Idle)
            {
                _state = PillState.Meeting;
                Apply(PillState.Meeting, PillState.Hidden, replace: true);
            }
        }
    }

    private void OnPillClicked(object? sender, EventArgs e)
    {
        try
        {
            if (_state == PillState.Recording) _app.StopDictation();
            else if (_state == PillState.Meeting) _app.ShowMainWindow("meetings");
        }
        catch (Exception ex)
        {
            Log.Error("Pill click failed", ex);
        }
    }

    public void Dispose()
    {
        _meter.Stop();
        _hideTimer.Stop();
        _hintTimer.Stop();
        _app.PropertyChanged -= OnAppChanged;
        _app.Dictation.PropertyChanged -= OnDictationChanged;
        _app.MeetingChanged -= OnMeetingChanged;
        _window.Close();
    }
}
