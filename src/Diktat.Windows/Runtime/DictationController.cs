using System.Windows.Threading;
using Diktat.AI;
using Diktat.Core.Settings;
using Diktat.Core.Text;
using Diktat.Transcription;
using Diktat.Windows.Platform;

namespace Diktat.Windows.Runtime;

/// One dictation at a time, from the shortcut to the text at the cursor (AppState.startWorkflow,
/// the workflows and handleWorkflowOutput of the Mac app):
/// record → transcribe → clean up → (AI mode) rewrite → fit to the field → paste → history.
/// The recording stays in memory and is never written to disk. UI thread only.
public sealed class DictationController : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly ClipboardPasteService _paste;
    private readonly AudioRecorder _recorder;
    private readonly SystemAudioMuter _muter;
    private readonly ForegroundTracker _foreground;
    private readonly Func<IntPtr, bool> _isOwnContentWindow;
    private readonly Func<bool> _meetingRecordsSystemAudio;
    private readonly DispatcherTimer _levelTimer;
    private readonly DispatcherTimer _resetTimer;

    private Session? _session;
    private int _sessionCounter;
    /// Start, stop and discard of the microphone run off the UI thread, one after the other, so
    /// a quick cancel and restart cannot stop the new recording.
    private Task _recorderQueue = Task.CompletedTask;
    private LastInsertion? _lastInsertion;

    private DictationPhase _phase;
    private WorkflowType? _activeMode;
    private string? _statusMessage;
    private float _audioLevel;
    private bool _noSignal;
    private string? _lastText;
    private string? _lastTranscript;
    private bool _lastTextWasOnlyCopied;
    private string? _microphoneName;

    /// A shortcut asked for a mode that cannot run (no speech model, no language model).
    public event EventHandler<WorkflowType>? ModeUnavailable;

    /// <param name="isOwnContentWindow">Whether a window handle is one of Diktat's own windows that
    /// must never receive the text (main window, setup assistant).</param>
    /// <param name="meetingRecordsSystemAudio">An online meeting records the PC's sound: then the
    /// output must not be muted.</param>
    public DictationController(AppServices services, GlobalHotkeyService hotkeys, ClipboardPasteService paste,
        ForegroundTracker foreground, Func<IntPtr, bool> isOwnContentWindow, Func<bool> meetingRecordsSystemAudio)
    {
        _services = services;
        _hotkeys = hotkeys;
        _paste = paste;
        _foreground = foreground;
        _isOwnContentWindow = isOwnContentWindow;
        _meetingRecordsSystemAudio = meetingRecordsSystemAudio;
        _recorder = new AudioRecorder(services.UiContext);
        _recorder.Interrupted += (_, error) =>
        {
            Log.Warn("Microphone interrupted while dictating", error);
            StopDictation();
        };
        _muter = new SystemAudioMuter();
        _levelTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _levelTimer.Tick += (_, _) =>
        {
            AudioLevel = _recorder.Level;
            NoSignal = _recorder.NoSignal;
        };
        _resetTimer = new DispatcherTimer();
        _resetTimer.Tick += (_, _) =>
        {
            _resetTimer.Stop();
            if (Phase is DictationPhase.Done or DictationPhase.Error) SetIdle();
        };
    }

    // ------------------------------------------------------------------ state

    public DictationPhase Phase { get => _phase; private set => SetProperty(ref _phase, value); }
    public WorkflowType? ActiveMode { get => _activeMode; private set => SetProperty(ref _activeMode, value); }
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public float AudioLevel { get => _audioLevel; private set => SetProperty(ref _audioLevel, value); }
    public bool NoSignal { get => _noSignal; private set => SetProperty(ref _noSignal, value); }
    public string? LastText { get => _lastText; private set => SetProperty(ref _lastText, value); }
    public string? LastTranscript { get => _lastTranscript; private set => SetProperty(ref _lastTranscript, value); }
    public bool LastTextWasOnlyCopied { get => _lastTextWasOnlyCopied; private set => SetProperty(ref _lastTextWasOnlyCopied, value); }
    /// The microphone of the running recording, for the label above the pill.
    public string? MicrophoneName { get => _microphoneName; private set => SetProperty(ref _microphoneName, value); }
    /// Recording or processing: no second dictation may start.
    public bool IsBusy => Phase is DictationPhase.Recording or DictationPhase.Processing;

    public bool IsModeAvailable(WorkflowType mode) =>
        DictationRules.IsModeAvailable(mode, _services.Transcription.HasModel, _services.Rewriter.CanRewrite(_services.Settings.App.RewriteEngine));

    public string? ModeUnavailableReason(WorkflowType mode) =>
        DictationRules.UnavailableReason(mode, _services.Transcription.HasModel, _services.Rewriter.CanRewrite(_services.Settings.App.RewriteEngine));

    // ------------------------------------------------------------------ hotkey events

    public void OnShortcutPressed(WorkflowType shortcut)
    {
        switch (Phase)
        {
            case DictationPhase.Recording:
                // Any shortcut ends a recording, also one started from the tray panel.
                StopDictation();
                return;
            case DictationPhase.Processing:
                // Never two dictations at once; the next press starts again.
                _hotkeys.ResetToggle();
                return;
            default:
                Start(shortcut, fromShortcut: true);
                return;
        }
    }

    public void OnShortcutReleased(WorkflowType shortcut)
    {
        if (Phase == DictationPhase.Recording) StopDictation();
    }

    public void OnShortcutCancelled() => CancelDictation();

    public void OnPotentialStart(WorkflowType shortcut)
    {
        if (!IsBusy) _recorder.Prewarm(_services.Settings.App.MicrophoneId, TimeSpan.FromSeconds(2));
    }

    // ------------------------------------------------------------------ start

    /// From the UI (tray panel, tray menu): the mode as chosen, the text into the window that
    /// was in front before Diktat's panel.
    public void StartDictation(WorkflowType mode) => Start(mode, fromShortcut: false);

    private void Start(WorkflowType mode, bool fromShortcut)
    {
        if (IsBusy) return;
        if (!IsModeAvailable(mode))
        {
            _hotkeys.ResetToggle();
            ShowResult(DictationPhase.Error, ModeUnavailableReason(mode), DictationRules.ErrorDuration);
            if (fromShortcut) ModeUnavailable?.Invoke(this, mode);
            return;
        }

        var settings = _services.Settings;
        var target = CaptureTarget();
        var app = target is null ? null : ForegroundAppInfo.FromWindow(target.Window);
        var session = new Session
        {
            Id = ++_sessionCounter,
            Mode = mode,
            Target = target,
            WebHost = app is { IsBrowser: true } ? SafeHostAsync(app) : Task.FromResult<string?>(null),
        };
        // Only the Diktat shortcut follows "Modus je App"; the tray starts the mode as chosen.
        session.ResolvedMode = fromShortcut ? ResolveModeAsync(session, mode, settings) : Task.FromResult(mode);
        // The text around the cursor, read while recording starts (never from a password field,
        // never from a window that is not in front).
        bool wantsField = settings.App.FitToSurroundingText || settings.App.AiSeesFieldContext;
        session.Field = wantsField && target is not null && IsInFront(target) ? SafeReadFieldAsync(target) : Task.FromResult<FocusedField?>(null);

        _resetTimer.Stop();
        _session = session;
        _lastInsertion = null; // a new dictation makes the offer for the previous one stale
        LastTranscript = null;
        ActiveMode = mode;
        StatusMessage = DictationRules.RecordingStatus;
        MicrophoneName = null;
        Phase = DictationPhase.Recording;
        _hotkeys.IsWorkflowBusy = true;
        Log.Info($"Dictation starts: {mode}{(fromShortcut ? " (shortcut)" : "")}, target {(target is null ? "none" : target.ExeName)}");

        if (settings.App.PlaysStartSound) RecordingSound.PlayStart();
        var microphone = settings.App.MicrophoneId;
        session.Started = OnRecorder(() => _recorder.Start(microphone));
        _ = AfterStartAsync(session, settings);
        _ = UpdateModeWhenResolvedAsync(session);
    }

    private async Task AfterStartAsync(Session session, SettingsContainer settings)
    {
        try
        {
            await session.Started;
        }
        catch (Exception ex)
        {
            if (_session != session) return;
            Log.Warn("Recording could not start", ex);
            _hotkeys.ResetToggle();
            Fail(session, RecordingFailure(ex));
            return;
        }
        if (_session != session || Phase != DictationPhase.Recording) return;
        MicrophoneName = _recorder.DeviceName;
        _levelTimer.Start();

        // An online meeting records the PC's sound; muting it would cut the call out.
        if (settings.App.MuteOutputWhileRecording && !_meetingRecordsSystemAudio())
        {
            // After the plop: a muted PC would not play it.
            if (settings.App.PlaysStartSound) await Task.Delay(RecordingSound.StartDuration + TimeSpan.FromMilliseconds(60));
            if (_session == session && Phase == DictationPhase.Recording) _muter.Mute();
        }
    }

    private async Task UpdateModeWhenResolvedAsync(Session session)
    {
        var mode = await session.ResolvedMode;
        if (_session == session && IsBusy && ActiveMode != mode)
        {
            Log.Info($"Mode by rule: {mode}");
            ActiveMode = mode;
        }
    }

    /// Where the text goes: the window in front, unless it is Diktat's. From the tray panel (or
    /// the tray menu, the taskbar) it is the window that was in front before.
    private PasteTarget? CaptureTarget()
    {
        var current = ForegroundAppInfo.Current();
        if (current is not null && !current.IsOwnProcess && !ForegroundTracker.IsShellWindow(current.Window)) return PasteTarget.Capture();
        if (current is not null && current.IsOwnProcess && _isOwnContentWindow(current.Window)) return null;
        var previous = _foreground.LastExternalApp;
        return previous is null ? null : new PasteTarget(previous.Window, previous.ProcessId, previous.ExeName, IntPtr.Zero);
    }

    private static bool IsInFront(PasteTarget target) => ForegroundAppInfo.Current()?.Window == target.Window;

    private async Task<WorkflowType> ResolveModeAsync(Session session, WorkflowType requested, SettingsContainer settings)
    {
        if (requested != WorkflowType.TapTalk || session.Target is null) return requested;
        var host = await session.WebHost;
        return ModeRules.Resolve(requested, session.Target.ExeName, host, settings.App, IsModeAvailable);
    }

    private static async Task<string?> SafeHostAsync(ForegroundApp app)
    {
        try
        {
            return await ForegroundAppInfo.GetWebHostAsync(app);
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the website failed", ex);
            return null;
        }
    }

    private static async Task<FocusedField?> SafeReadFieldAsync(PasteTarget target)
    {
        try
        {
            var field = await FocusedFieldReader.ReadAsync();
            return field is not null && field.ProcessId == target.ProcessId ? field : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the focused field failed", ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ stop and cancel

    /// Every way of ending a recording comes here (shortcut, pill, tray panel).
    public async void StopDictation()
    {
        var session = _session;
        if (session is null || Phase != DictationPhase.Recording) return;
        _hotkeys.ResetToggle();
        StopMeter();
        Phase = DictationPhase.Processing;
        StatusMessage = DictationRules.TranscribingStatus;

        float[] samples;
        bool silent;
        try
        {
            await session.Started;
            samples = await OnRecorder(_recorder.Stop);
            silent = _recorder.HeardOnlySilence;
        }
        catch (Exception ex)
        {
            if (_session == session) Fail(session, RecordingFailure(ex));
            return;
        }
        finally
        {
            // Sound comes back as soon as the recording ends, not after the transcription.
            if (_session == session || _session is null) _muter.Restore();
        }
        if (_session != session) return;

        // The text goes into the field that has the focus now: clicking into the right field
        // while still talking is fine; only a move while processing sends it to the clipboard.
        session.TargetAtStop = RefreshTargetAsync(session.Target);

        var duration = TimeSpan.FromSeconds((double)samples.Length / AudioRecorder.SampleRate);
        if (TranscriptionQualityService.ShouldRejectRecording(duration))
        {
            Fail(session, DictationRules.NothingRecognized);
            return;
        }
        if (silent)
        {
            Fail(session, DictationRules.OnlySilence);
            return;
        }
        await ProcessAsync(session, samples, duration);
    }

    /// Escape, or a Windows shortcut typed over the held keys: the recording or the running
    /// processing is thrown away.
    public void CancelDictation()
    {
        var session = _session;
        if (session is null || session.IsPasting) return;
        _hotkeys.ResetToggle();
        if (Phase == DictationPhase.Recording)
        {
            StopMeter();
            _ = DiscardAsync(session);
        }
        _muter.Restore();
        session.Cancel.Cancel();
        Log.Info("Dictation cancelled");
        _session = null;
        SetIdle();
    }

    private async Task DiscardAsync(Session session)
    {
        try
        {
            await OnRecorder(_recorder.Discard);
        }
        catch (Exception ex)
        {
            Log.Warn("Discarding the recording failed", ex);
        }
    }

    private static async Task<PasteTarget?> RefreshTargetAsync(PasteTarget? target)
    {
        if (target is null || !IsInFront(target)) return target;
        var refreshed = target with { FocusWindow = ForegroundAppInfo.FocusedWindow(target.Window) };
        try
        {
            return refreshed with { Focus = await FocusedFieldReader.CaptureFocusSpotAsync() };
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the focus failed", ex);
            return refreshed;
        }
    }

    // ------------------------------------------------------------------ processing

    private async Task ProcessAsync(Session session, float[] samples, TimeSpan duration)
    {
        var ct = session.Cancel.Token;
        var settings = _services.Settings;
        var language = settings.Transcription.Language;
        try
        {
            var transcription = _services.Transcription;
            if (!transcription.IsReady)
            {
                StatusMessage = DictationRules.LoadingModelStatus;
                await transcription.WarmUpAsync(ct);
                if (_session != session) return;
                StatusMessage = DictationRules.TranscribingStatus;
            }
            var result = await transcription.TranscribeAsync(samples, language, ct);
            if (_session != session) return;

            var cleaned = TranscriptionQualityService.CleanedTranscript(result.Text);
            if (TranscriptionQualityService.IsLikelyArtifact(cleaned, duration))
            {
                Fail(session, DictationRules.NothingRecognized);
                return;
            }
            var postProcessor = TranscriptPostProcessor.FromSettings(settings, _services.SpellChecker);
            var processed = postProcessor.Process(cleaned).Trim();
            if (processed.Length == 0)
            {
                Fail(session, DictationRules.NothingRecognized);
                return;
            }

            var mode = await session.ResolvedMode;
            var host = await session.WebHost;
            var tone = settings.App.StyleSettings.ToneFor(StyleContextInfo.Detect(session.Target?.ExeName, host));
            string text;
            string? transcript = null;
            if (DictationRules.IsPlainDictation(mode))
            {
                if (settings.App.SpeechCleanupLevel.UsesLanguageModel() && _services.Rewriter.CanRewrite(settings.App.RewriteEngine))
                {
                    StatusMessage = DictationRules.PolishingStatus;
                    processed = await _services.SpeechPolisher.PolishAsync(processed, SpeechPolishConfiguration.From(settings, tone), ct);
                    if (_session != session) return;
                }
                text = tone.Apply(processed);
                // The AI modes shape their own paragraphs; word-for-word dictation gets them here.
                if (settings.App.AutomaticParagraphs) text = TranscriptParagraphService.Format(text, language);
            }
            else
            {
                transcript = processed;
                StatusMessage = ModeRewriter.StatusText(mode);
                var field = await session.Field;
                var context = DictationRules.AiContext(field, settings.App.AiSeesFieldContext);
                var rewritten = await _services.ModeRewriter.RewriteAsync(mode, processed, settings, context, ct);
                if (_session != session) return;
                text = TranscriptionQualityService.CleanedTranscript(rewritten);
            }
            if (text.Length == 0)
            {
                Fail(session, DictationRules.NothingRecognized);
                return;
            }
            await DeliverAsync(session, text, transcript, mode, settings);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled with Escape; the state is reset already.
        }
        catch (NothingRecognizedException)
        {
            Fail(session, DictationRules.NothingRecognized);
        }
        catch (Exception ex) when (ex is TranscriptionException or TextRewriterException or AIClientException)
        {
            Log.Warn("Dictation failed", ex);
            Fail(session, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("Dictation failed unexpectedly", ex);
            Fail(session, "Etwas ist schiefgelaufen: " + ex.Message);
        }
    }

    private async Task DeliverAsync(Session session, string text, string? transcript, WorkflowType mode, SettingsContainer settings)
    {
        session.IsPasting = true;
        LastText = text;
        LastTranscript = transcript;
        _services.History.Add(text, mode);

        var field = await session.Field;
        bool fit = settings.App.FitToSurroundingText;
        var output = DictationRules.FitToField(text, field, fit);
        var fittedTranscript = transcript is null ? null : DictationRules.FitToField(transcript, field, fit);
        var target = session.TargetAtStop is { } refreshing ? await refreshing : session.Target;

        PasteOutcome outcome;
        if (target is null)
        {
            // Started in Diktat's own window: the text waits on the clipboard.
            outcome = await _paste.CopyOnlyAsync(output) ? PasteOutcome.CopiedNoTarget : PasteOutcome.Failed;
        }
        else
        {
            outcome = (await _paste.PasteAsync(output, target, settings.App.RestoreClipboardAfterPaste)).Outcome;
        }
        if (_session != session) return;
        Log.Info($"Dictation delivered: {outcome}");

        LastTextWasOnlyCopied = outcome != PasteOutcome.Pasted;
        _lastInsertion = outcome == PasteOutcome.Pasted && target is not null && fittedTranscript is not null && fittedTranscript != output
            ? new LastInsertion(output, fittedTranscript, target, DateTimeOffset.Now)
            : null;
        _session = null;
        if (outcome == PasteOutcome.Failed)
        {
            ShowResult(DictationPhase.Error, DictationRules.StatusFor(outcome), DictationRules.ErrorDuration);
        }
        else
        {
            ShowResult(DictationPhase.Done, DictationRules.StatusFor(outcome),
                outcome == PasteOutcome.Pasted ? DictationRules.DoneDuration : DictationRules.CopiedDuration);
        }
    }

    // ------------------------------------------------------------------ after the text

    /// After an AI mode: replaces the pasted text with what was said. Where the field does not
    /// allow it, the word-for-word text goes to the clipboard.
    public async void InsertTranscriptInstead()
    {
        var transcript = LastTranscript;
        if (transcript is null || IsBusy) return;
        var last = _lastInsertion;
        _lastInsertion = null;
        LastTranscript = null;
        LastText = transcript;
        var settings = _services.Settings;

        bool replaced = false;
        if (last is not null && DateTimeOffset.Now - last.At < DictationRules.ReplaceOfferDuration)
        {
            try
            {
                var target = last.Target with { FocusWindow = IntPtr.Zero, Focus = null };
                if (await TranscriptReplacer.SelectInsertedAsync(target, last.Inserted))
                {
                    var result = await _paste.PasteAsync(last.Transcript, target, settings.App.RestoreClipboardAfterPaste);
                    replaced = result.Outcome == PasteOutcome.Pasted;
                    if (!replaced && result.Outcome != PasteOutcome.Failed)
                    {
                        LastTextWasOnlyCopied = true;
                        ShowResult(DictationPhase.Done, DictationRules.ReplaceFailed, DictationRules.CopiedDuration);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Replacing the AI text failed", ex);
            }
        }
        if (replaced)
        {
            LastTextWasOnlyCopied = false;
            ShowResult(DictationPhase.Done, DictationRules.PastedStatus, DictationRules.DoneDuration);
            return;
        }
        await _paste.CopyOnlyAsync(last?.Transcript ?? transcript);
        LastTextWasOnlyCopied = true;
        ShowResult(DictationPhase.Done, DictationRules.ReplaceFailed, DictationRules.CopiedDuration);
    }

    public void CopyToClipboard(string text) => _ = CopyAsync(text);

    private async Task CopyAsync(string text)
    {
        if (!await _paste.CopyOnlyAsync(text)) Log.Warn("Copying to the clipboard failed");
    }

    private static string RecordingFailure(Exception ex) =>
        ex is AudioCaptureException ? ex.Message : "Die Aufnahme ließ sich nicht starten: " + ex.Message;

    private Task OnRecorder(Action action) => OnRecorder(() =>
    {
        action();
        return true;
    });

    private Task<T> OnRecorder<T>(Func<T> action)
    {
        var task = _recorderQueue.ContinueWith(_ => action(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        _recorderQueue = task;
        return task;
    }

    // ------------------------------------------------------------------ phase helpers

    private void Fail(Session session, string? message)
    {
        if (_session != session) return;
        _session = null;
        StopMeter();
        _muter.Restore();
        Log.Info("Dictation ended with an error");
        ShowResult(DictationPhase.Error, message, DictationRules.ErrorDuration);
    }

    private void ShowResult(DictationPhase phase, string? message, TimeSpan duration)
    {
        _hotkeys.IsWorkflowBusy = false;
        ActiveMode = null;
        StatusMessage = message;
        Phase = phase;
        _resetTimer.Stop();
        _resetTimer.Interval = duration;
        _resetTimer.Start();
    }

    private void SetIdle()
    {
        _resetTimer.Stop();
        _hotkeys.IsWorkflowBusy = false;
        StopMeter();
        ActiveMode = null;
        StatusMessage = null;
        Phase = DictationPhase.Idle;
    }

    private void StopMeter()
    {
        _levelTimer.Stop();
        AudioLevel = 0;
        NoSignal = false;
    }

    /// After an unexpected error somewhere in the app: a dictation that lost its recording ends,
    /// and the sound never stays off while nothing records.
    public void RecoverAfterError()
    {
        if (Phase == DictationPhase.Recording && _session?.Started.IsCompleted == true && !_recorder.IsRecording)
        {
            CancelDictation();
        }
        if (!IsBusy) _muter.Restore();
    }

    /// Diktat quits: nothing may stay muted, nothing half-recorded stays in memory.
    public void Shutdown()
    {
        _session?.Cancel.Cancel();
        _session = null;
        _levelTimer.Stop();
        _resetTimer.Stop();
        try { _recorder.Discard(); } catch (Exception ex) { Log.Warn("Discarding on exit failed", ex); }
        _muter.Restore();
    }

    public void Dispose()
    {
        Shutdown();
        _recorder.Dispose();
        _muter.Dispose();
    }

    private sealed class Session
    {
        public required int Id { get; init; }
        public required WorkflowType Mode { get; init; }
        public required PasteTarget? Target { get; init; }
        public required Task<string?> WebHost { get; init; }
        public Task<WorkflowType> ResolvedMode { get; set; } = Task.FromResult(WorkflowType.TapTalk);
        public Task<FocusedField?> Field { get; set; } = Task.FromResult<FocusedField?>(null);
        public Task Started { get; set; } = Task.CompletedTask;
        public Task<PasteTarget?>? TargetAtStop { get; set; }
        public CancellationTokenSource Cancel { get; } = new();
        /// The text is on its way into the field; Escape no longer stops it.
        public bool IsPasting { get; set; }
    }

    private sealed record LastInsertion(string Inserted, string Transcript, PasteTarget Target, DateTimeOffset At);
}
