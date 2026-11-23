using System.IO;
using System.Windows.Threading;
using Diktat.AI;
using Diktat.AI.Meetings;
using Diktat.Core.Settings;
using Diktat.Core.Text;
using Diktat.Transcription;
using Diktat.Transcription.Meeting;
using Diktat.Windows.Platform;
using AiTimedWord = Diktat.AI.Meetings.TimedWord;
using MeetingTranscript = Diktat.Transcription.Meeting.MeetingTranscript;

namespace Diktat.Windows.Runtime;

/// One meeting: records the microphone, what the PC plays, or both (see <see cref="MeetingSource"/>)
/// into WAV files in chunks, optionally writes live notes, then transcribes by person, cleans up
/// each paragraph, saves the meeting and summarizes it on this PC (port of MeetingController.swift).
/// Everything stays on this PC; the raw audio is deleted once the transcript is saved.
/// UI thread only.
public sealed class MeetingSession : ObservableObject, IMeetingSession
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly AppServices _services;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _flushTimer;
    private readonly MeetingProblems _problems = new();
    private readonly MicrophoneSilenceTracker _silence = new();
    private DispatcherTimer? _liveNotesTimer;
    private MeetingRecorder? _recorder;
    private TrackWriter? _micWriter;
    private TrackWriter? _systemWriter;
    private bool _aligned;
    private Diktat.AI.Meetings.LiveNotes? _liveNotes;
    private List<float>? _liveMic;
    private List<float>? _liveSystem;
    private Task _liveNotesShutdown = Task.CompletedTask;
    private CancellationTokenSource? _processing;
    private Guid _id;
    private bool _starting;

    private MeetingPhase _phase;
    private bool _isOnline;
    private bool _recordsMicrophone;
    private string? _microphoneName;
    private MeetingProblem? _problem;
    private TimeSpan _elapsed;
    private float _microphoneLevel;
    private float _systemAudioLevel;
    private string? _statusMessage;
    private string _liveNotesText = "";
    private Guid? _meetingId;
    private string? _errorMessage;

    /// The evaluation ended: the saved meeting, or null when it failed (see ErrorMessage).
    public event EventHandler<MeetingRecord?>? Finished;

    public MeetingSession(AppServices services)
    {
        _services = services;
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _meterTimer.Tick += (_, _) => Meter();
        _flushTimer = new DispatcherTimer { Interval = FlushInterval };
        _flushTimer.Tick += (_, _) =>
        {
            if (_recorder is { } recorder) Flush(recorder, final: false);
        };
    }

    public MeetingPhase Phase { get => _phase; private set => SetProperty(ref _phase, value); }
    public bool IsOnline { get => _isOnline; private set => SetProperty(ref _isOnline, value); }
    public bool RecordsMicrophone { get => _recordsMicrophone; private set => SetProperty(ref _recordsMicrophone, value); }
    public string? MicrophoneName { get => _microphoneName; private set => SetProperty(ref _microphoneName, value); }
    public MeetingProblem? Problem { get => _problem; private set => SetProperty(ref _problem, value); }
    public TimeSpan Elapsed { get => _elapsed; private set => SetProperty(ref _elapsed, value); }
    public float MicrophoneLevel { get => _microphoneLevel; private set => SetProperty(ref _microphoneLevel, value); }
    public float SystemAudioLevel { get => _systemAudioLevel; private set => SetProperty(ref _systemAudioLevel, value); }
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string LiveNotes { get => _liveNotesText; private set => SetProperty(ref _liveNotesText, value); }
    public Guid? MeetingId { get => _meetingId; private set => SetProperty(ref _meetingId, value); }
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

    /// The PC's sound is being recorded: the output must not be muted now.
    public bool RecordsSystemAudio => Phase == MeetingPhase.Recording && IsOnline;

    private static string? OwnName => DictationRules.FirstName(RuntimeNative.UserDisplayName());

    // ------------------------------------------------------------------ recording

    /// Recording, evaluating, or the microphone is being opened.
    public bool IsBusy => Phase != MeetingPhase.Idle || _starting;

    public async Task StartAsync(MeetingSource source)
    {
        if (IsBusy || _recorder is not null) return;
        _starting = true;
        try
        {
            await StartCoreAsync(source);
        }
        finally
        {
            _starting = false;
        }
    }

    private async Task StartCoreAsync(MeetingSource source)
    {
        ErrorMessage = null;
        StatusMessage = null;
        if (!_services.Transcription.HasModel)
        {
            ErrorMessage = TranscriptionException.NoModelMessage;
            return;
        }

        bool withMicrophone = source.UsesMicrophone();
        bool withSystemAudio = source.UsesSystemAudio();
        var startedAt = DateTimeOffset.Now;
        var record = new MeetingRecord { CreatedAt = startedAt, Title = MeetingRecord.TitleFor(startedAt), Status = MeetingStatus.Pending };
        _id = record.Id;
        var recorder = new MeetingRecorder(_services.UiContext);
        // A monitor connected for the presentation or a microphone unplugged must not end the meeting.
        recorder.Microphone.SurvivesDeviceChanges = true;
        var microphone = _services.Settings.App.MicrophoneId;
        Exception? systemError = null;
        try
        {
            systemError = await Task.Run(() => recorder.Start(microphone, withMicrophone, withSystemAudio));
        }
        catch (Exception ex)
        {
            recorder.Dispose();
            Log.Warn("Meeting recording could not start", ex);
            ErrorMessage = !withMicrophone
                // Without the microphone the PC's sound is the whole meeting.
                ? L.F("Das System-Audio lässt sich nicht aufnehmen. ({0})", ex.Message)
                : ex is AudioCaptureException ? ex.Message : L.F("Die Aufnahme ließ sich nicht starten: {0}", ex.Message);
            return;
        }
        bool online = withSystemAudio && systemError is null;

        // The files first: a recorder that runs without them would fill the memory unseen, and
        // this session could never start again.
        TrackWriter? micWriter = null;
        TrackWriter? systemWriter = null;
        try
        {
            micWriter = withMicrophone ? new TrackWriter(MeetingAudio.MicrophonePath(_id), AudioRecorder.SampleRate) : null;
            systemWriter = online ? new TrackWriter(MeetingAudio.SystemAudioPath(_id), LoopbackRecorder.SampleRate) : null;
        }
        catch (Exception ex)
        {
            if (micWriter is not null) await micWriter.CloseAsync();
            await Task.Run(recorder.Dispose);
            MeetingAudio.Delete(_id);
            Log.Warn("Meeting audio files could not be created", ex);
            ErrorMessage = L.F("Die Aufnahme ließ sich nicht speichern: {0}", ex.Message);
            return;
        }

        _recorder = recorder;
        IsOnline = online;
        RecordsMicrophone = withMicrophone;
        MicrophoneName = withMicrophone ? recorder.Microphone.DeviceName : null;
        if (withSystemAudio && systemError is not null)
        {
            StatusMessage = L.F("Das System-Audio lässt sich nicht aufnehmen, es läuft nur das Mikrofon. ({0})", systemError.Message);
        }
        recorder.Microphone.Interrupted += OnMicrophoneInterrupted;
        recorder.Microphone.DeviceChanged += OnMicrophoneChanged;
        recorder.SystemAudio.OutputChanged += OnOutputChanged;
        _micWriter = micWriter;
        _systemWriter = systemWriter;
        // Two tracks start at the same moment; a single one needs no alignment.
        _aligned = micWriter is null || systemWriter is null;
        // Stored as pending from the start, so a crash or quit keeps the audio for a later evaluation.
        _services.Meetings.Upsert(record);
        MeetingId = null;
        Elapsed = TimeSpan.Zero;
        RuntimeNative.KeepAwake(true);
        StartLiveNotes();
        _meterTimer.Start();
        _flushTimer.Start();
        Phase = MeetingPhase.Recording;
        Log.Info($"Meeting recording starts ({source}{(withSystemAudio && !online ? ", without system audio" : "")})");
    }

    private void SetProblem(MeetingProblem problem, bool isActive)
    {
        if (_problems.Set(problem, isActive)) Problem = _problems.Current;
    }

    private void ClearProblems()
    {
        _problems.Clear();
        _silence.Reset();
        Problem = null;
    }

    /// Headphones unplugged or the output gone: the loopback moves to the current output by itself.
    private void OnOutputChanged(object? sender, Exception? error)
    {
        if (Phase != MeetingPhase.Recording) return;
        SetProblem(MeetingProblem.SystemAudioLost, error is not null);
    }

    /// The audio setup changed under the recording and the microphone records again, or not yet.
    private void OnMicrophoneChanged(object? sender, AudioCaptureException? error)
    {
        if (Phase != MeetingPhase.Recording || _recorder is not { } recorder) return;
        SetProblem(MeetingProblem.MicrophoneLost, error is not null);
        if (error is not null)
        {
            Log.Warn("Meeting microphone lost, trying again", error);
            return;
        }
        var name = recorder.Microphone.DeviceName;
        if (!string.IsNullOrEmpty(name) && name != MicrophoneName)
        {
            // Said once, so nobody wonders why the far end of the table got quiet.
            StatusMessage = L.F("Das Mikrofon hat gewechselt. Die Aufnahme läuft mit „{0}“ weiter.", name);
        }
        MicrophoneName = string.IsNullOrEmpty(name) ? MicrophoneName : name;
    }

    /// Only without SurvivesDeviceChanges, which the meeting sets; kept as the last resort.
    private void OnMicrophoneInterrupted(object? sender, AudioCaptureException error)
    {
        if (Phase != MeetingPhase.Recording) return;
        Log.Warn("Meeting microphone interrupted", error);
        StatusMessage = L.T("Die Aufnahme wurde unterbrochen, zum Beispiel weil das Mikrofon getrennt wurde. Ausgewertet wird, was bis dahin aufgenommen wurde.");
        Stop();
    }

    private void Meter()
    {
        if (_recorder is not { } recorder) return;
        if (RecordsMicrophone)
        {
            MicrophoneLevel = recorder.Microphone.Level;
            SetProblem(MeetingProblem.MicrophoneSilent, _silence.IsSilentTooLong(recorder.Microphone.CurrentDecibels, DateTimeOffset.Now));
        }
        SystemAudioLevel = IsOnline ? recorder.SystemAudio.Level : 0;
        var duration = RecordsMicrophone ? recorder.Microphone.Duration : TimeSpan.Zero;
        if (IsOnline && recorder.SystemAudio.Duration > duration) duration = recorder.SystemAudio.Duration;
        Elapsed = TimeSpan.FromSeconds(Math.Floor(duration.TotalSeconds));
    }

    /// Moves the recorded samples from memory to the files. Two tracks start at the same moment:
    /// the later one gets silence in front, once the first microphone sample has arrived (at the
    /// end of a very short recording with what is known by then).
    private void Flush(MeetingRecorder recorder, bool final)
    {
        if (_micWriter is null && _systemWriter is null) return;
        if (!_aligned)
        {
            long micStart = recorder.Microphone.FirstSampleTimestamp;
            long systemStart = recorder.SystemAudio.FirstSampleTimestamp;
            if (micStart == 0 && !final) return; // wait for the first microphone packet
            _micWriter?.AppendSilence(MeetingAudio.LeadingPad(micStart, systemStart, AudioRecorder.SampleRate));
            _systemWriter?.AppendSilence(MeetingAudio.LeadingPad(systemStart, micStart, LoopbackRecorder.SampleRate));
            _aligned = true;
        }
        Append(_micWriter is not null ? recorder.Microphone.TakeSamples() : [],
            _systemWriter is not null && recorder.SystemAudio.IsRecording ? recorder.SystemAudio.TakeSamples() : []);
    }

    private void Append(float[] mic, float[] system)
    {
        _micWriter?.Append(mic);
        _systemWriter?.Append(system);
        _liveMic?.AddRange(mic);
        _liveSystem?.AddRange(system);
    }

    /// Ends the recording and starts the evaluation.
    public async void Stop()
    {
        if (Phase != MeetingPhase.Recording || _recorder is not { } recorder) return;
        var notice = StatusMessage;
        bool withMicrophone = RecordsMicrophone;
        Phase = MeetingPhase.Processing;
        StatusMessage = DictationRules.TranscribingStatus;
        // Writers and live buffers belong to the UI thread; only the stopping runs elsewhere.
        StopTimers();
        Flush(recorder, final: true);
        var result = await Task.Run(() => StopRecorders(recorder));
        Append(result.Mic, result.System);
        await CloseWritersAsync();
        bool systemSilent = FinishStorage(result);
        if (systemSilent && withMicrophone)
        {
            notice = Join(notice, SystemAudioSilentNotice);
        }
        await ProcessAsync(_id, notice, systemSilent);
    }

    /// Nothing came from the PC's sound. On Windows that is mostly a call app that plays through
    /// another device than the Windows default output, which is what the loopback records.
    private static string SystemAudioSilentNotice =>
        L.T("Vom System-Audio kam nichts an. Diktat nimmt auf, was über das Standard-Ausgabegerät von Windows läuft. Spielte die Anruf-App über ein anderes Gerät? Ausgewertet wird nur das Mikrofon.");

    /// Throws a running recording away, audio included; a running evaluation stops (the meeting
    /// stays pending and can be evaluated again).
    public async void Cancel()
    {
        if (Phase == MeetingPhase.Processing)
        {
            _processing?.Cancel();
            return;
        }
        if (Phase != MeetingPhase.Recording || _recorder is not { } recorder) return;
        StopTimers();
        _recorder = null;
        await Task.Run(recorder.Discard);
        Detach(recorder);
        await CloseWritersAsync();
        StopLiveNotes();
        MeetingAudio.Delete(_id);
        _services.Meetings.Delete(_id);
        RuntimeNative.KeepAwake(false);
        ResetLevels();
        ClearProblems();
        MicrophoneName = null;
        StatusMessage = null;
        Phase = MeetingPhase.Idle;
        Log.Info("Meeting discarded");
    }

    /// Diktat quits: the files are closed properly; the meeting stays pending.
    public void FinishBeforeQuit()
    {
        _processing?.Cancel();
        if (Phase != MeetingPhase.Recording || _recorder is not { } recorder) return;
        try
        {
            StopTimers();
            Flush(recorder, final: true);
            var result = StopRecorders(recorder);
            Append(result.Mic, result.System);
            var mic = _micWriter;
            var system = _systemWriter;
            _micWriter = null;
            _systemWriter = null;
            Task.Run(async () =>
            {
                if (mic is not null) await mic.CloseAsync().ConfigureAwait(false);
                if (system is not null) await system.CloseAsync().ConfigureAwait(false);
            }).Wait(TimeSpan.FromSeconds(5));
            FinishStorage(result);
        }
        catch (Exception ex)
        {
            Log.Warn("Closing the meeting recording failed", ex);
        }
    }

    private sealed record StoppedTracks(MeetingRecorder Recorder, float[] Mic, float[] System, double Duration, bool Online, float Peak);

    /// Stops both recorders (blocks up to a second each) and returns what was still in memory.
    private StoppedTracks StopRecorders(MeetingRecorder recorder)
    {
        bool online = IsOnline;
        bool withMicrophone = RecordsMicrophone;
        double duration = Math.Max(
            withMicrophone ? recorder.Microphone.Duration.TotalSeconds : 0,
            online ? recorder.SystemAudio.Duration.TotalSeconds : 0);
        float peak = 0;
        float[] mic = [];
        float[] system = [];
        try
        {
            if (withMicrophone) mic = recorder.Microphone.Stop();
            if (online)
            {
                peak = recorder.SystemAudio.Peak;
                system = recorder.SystemAudio.Stop();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Stopping the meeting recording failed", ex);
        }
        return new StoppedTracks(recorder, mic, system, duration, online, peak);
    }

    /// After the recording: timers, recorder and live notes go; the duration is stored. True when
    /// the PC's sound brought nothing (its file is dropped then).
    private bool FinishStorage(StoppedTracks result)
    {
        StopTimers();
        _recorder = null;
        Detach(result.Recorder);
        StopLiveNotes();
        ResetLevels();
        ClearProblems();
        MicrophoneName = null;
        if (_services.Meetings.Meeting(_id) is { } record)
        {
            record.Duration = result.Duration;
            _services.Meetings.Upsert(record);
        }
        bool silent = result.Online && result.Peak <= 0;
        if (silent)
        {
            try { File.Delete(MeetingAudio.SystemAudioPath(_id)); } catch (Exception ex) { Log.Warn("Dropping silent system audio failed", ex); }
        }
        return silent;
    }

    private void Detach(MeetingRecorder recorder)
    {
        recorder.Microphone.Interrupted -= OnMicrophoneInterrupted;
        recorder.Microphone.DeviceChanged -= OnMicrophoneChanged;
        recorder.SystemAudio.OutputChanged -= OnOutputChanged;
        recorder.Dispose();
    }

    private async Task CloseWritersAsync()
    {
        var mic = _micWriter;
        var system = _systemWriter;
        _micWriter = null;
        _systemWriter = null;
        if (mic is not null) await mic.CloseAsync().ConfigureAwait(true);
        if (system is not null) await system.CloseAsync().ConfigureAwait(true);
    }

    private void StopTimers()
    {
        _meterTimer.Stop();
        _flushTimer.Stop();
        _liveNotesTimer?.Stop();
        _liveNotesTimer = null;
    }

    private void ResetLevels()
    {
        MicrophoneLevel = 0;
        SystemAudioLevel = 0;
    }

    // ------------------------------------------------------------------ live notes

    /// Live notes need a model on this PC (the built-in model, Ollama, LM Studio); the text never
    /// leaves the PC. "Immer Modell-Anbieter" puts the server first, like for the summary.
    private void StartLiveNotes()
    {
        var server = LiveNotesBackend.ServerOnThisPC(_services.Rewriter);
        var builtIn = LiveNotesBackend.BuiltIn(_services.Rewriter);
        var backend = _services.Settings.App.RewriteEngine == RewriteEngine.Provider ? server ?? builtIn : builtIn ?? server;
        if (backend is null) return;
        var notes = new Diktat.AI.Meetings.LiveNotes(_ => Task.FromResult(backend));
        notes.Changed += (_, _) => LiveNotes = notes.Notes;
        _liveNotes = notes;
        _liveMic = new List<float>();
        _liveSystem = IsOnline ? new List<float>() : null;
        _liveNotesTimer = new DispatcherTimer { Interval = Diktat.AI.Meetings.LiveNotes.Interval };
        _liveNotesTimer.Tick += (_, _) => _ = UpdateLiveNotesAsync();
        _liveNotesTimer.Start();
    }

    private async Task UpdateLiveNotesAsync()
    {
        if (_liveNotes is not { IsUpdating: false } notes || _liveMic is null) return;
        var mic = _liveMic.ToArray();
        var system = _liveSystem?.ToArray();
        _liveMic.Clear();
        _liveSystem?.Clear();
        var settings = _services.Settings;
        var post = TranscriptPostProcessor.FromSettings(settings, _services.SpellChecker);
        try
        {
            await notes.UpdateAsync(async ct =>
            {
                var language = settings.Transcription.Language;
                // "Nur System-Audio" brings no microphone samples.
                IReadOnlyList<AiTimedWord> own = mic.Length > 0
                    ? Words(await _services.Transcription.TranscribeAllowingEmptyAsync(mic, language, ct))
                    : [];
                IReadOnlyList<AiTimedWord> others = [];
                if (system is { Length: > 0 })
                {
                    others = Words(await _services.Transcription.TranscribeAllowingEmptyAsync(system, language, ct));
                }
                return Diktat.AI.Meetings.LiveNotes.ComposeStretch(own, others, OwnName, post.Process);
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Live notes update failed", ex);
        }
    }

    private static List<AiTimedWord> Words(TranscriptionResult result) =>
        result.Words.Select(w => new AiTimedWord(w.Text, w.Start, w.End)).ToList();

    /// Ends the notes and frees their model; the summary waits for that.
    private void StopLiveNotes()
    {
        var notes = _liveNotes;
        _liveNotes = null;
        _liveMic = null;
        _liveSystem = null;
        if (notes is not null) _liveNotesShutdown = SafeStopAsync(notes);
    }

    private static async Task SafeStopAsync(Diktat.AI.Meetings.LiveNotes notes)
    {
        try
        {
            await notes.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Stopping live notes failed", ex);
        }
    }

    // ------------------------------------------------------------------ evaluation

    /// Evaluates a stored meeting whose audio is still there (after Stop, or again after a failure).
    public Task ProcessAsync(Guid meetingId) => ProcessAsync(meetingId, null, systemAudioSilent: false);

    /// <param name="systemAudioSilent">The recording just ended and the PC's sound brought nothing.</param>
    private async Task ProcessAsync(Guid meetingId, string? notice, bool systemAudioSilent)
    {
        if (_services.Meetings.Meeting(meetingId) is not { } record)
        {
            ErrorMessage = L.T("Das Meeting gibt es nicht mehr.");
            Phase = MeetingPhase.Idle;
            return;
        }
        _id = meetingId;
        var cancel = new CancellationTokenSource();
        _processing = cancel;
        var settings = _services.Settings;
        ErrorMessage = null;
        MeetingId = null;
        Phase = MeetingPhase.Processing;
        StatusMessage = DictationRules.TranscribingStatus;
        RuntimeNative.KeepAwake(true);
        var status = new Progress<string>(line => { if (Phase == MeetingPhase.Processing) StatusMessage = line; });
        try
        {
            var micPath = MeetingAudio.MicrophonePath(meetingId);
            var systemPath = MeetingAudio.SystemAudioPath(meetingId);
            // "Nur System-Audio" has no microphone file, and nothing may have arrived at all.
            bool hasMicrophone = File.Exists(micPath);
            bool hasSystemAudio = File.Exists(systemPath);
            if (!hasMicrophone && !hasSystemAudio)
            {
                throw new MeetingTranscriptionException(systemAudioSilent
                    ? L.T("Von diesem Meeting ist kein Ton da. Vom System-Audio kam nichts an. Diktat nimmt auf, was über das Standard-Ausgabegerät von Windows läuft. Spielte die Anruf-App über ein anderes Gerät?")
                    : L.T("Die Aufnahme dieses Meetings ist nicht mehr da."));
            }
            var (mic, system) = await Task.Run(() =>
            {
                float[]? micTrack = hasMicrophone ? MeetingAudio.Read(micPath) : null;
                float[]? systemTrack = hasSystemAudio ? MeetingAudio.Read(systemPath) : null;
                if (micTrack is null || systemTrack is null) return (micTrack, systemTrack);
                var (a, b) = MeetingAudio.SameLength(micTrack, systemTrack);
                return ((float[]?)a, (float[]?)b);
            }, cancel.Token);
            if (record.Duration <= 0) record.Duration = (double)(mic ?? system!).Length / AudioRecorder.SampleRate;

            MeetingTranscript transcript;
            using (var diarizer = SherpaSpeakerDiarizer.FromStore(_services.Models))
            {
                var transcriber = new MeetingTranscriber(_services.Transcription, () => diarizer);
                transcript = await transcriber.TranscribeAsync(mic, system, settings.Transcription.Language, status, cancel.Token);
            }
            mic = null;
            system = null;
            notice = Join(notice, transcript.Notice);

            var post = TranscriptPostProcessor.FromSettings(settings, _services.SpellChecker);
            var turns = transcript.Segments
                .Select(s => new MeetingTurn(s.Speaker, s.Start, TranscriptionQualityService.CleanedTranscript(post.Process(s.Text))))
                .Where(t => t.Text.Length > 0)
                .ToList();
            if (turns.Count == 0) throw new MeetingTranscriptionException(L.T("In der Aufnahme wurde keine Sprache erkannt."));
            record.Turns = turns;

            if (settings.App.SummarizesMeetings)
            {
                // The live notes may still hold the model; only one copy fits comfortably.
                await _liveNotesShutdown;
                StatusMessage = L.T("Wird zusammengefasst ...");
                var summary = await _services.MeetingSummaries.SummarizeAsync(record, OwnName, null,
                    prefersServerOnThisPC: settings.App.RewriteEngine == RewriteEngine.Provider, cancel.Token);
                record.Summary = summary.Summary;
                record.SummaryNote = summary.Note;
            }

            record.Status = MeetingStatus.Done;
            // The audio goes only once the transcript is safely on disk.
            if (_services.Meetings.Upsert(record))
            {
                MeetingAudio.Delete(meetingId);
            }
            else
            {
                notice = Join(notice, L.T("Das Meeting ließ sich nicht sichern, zum Beispiel weil der Speicher voll ist. Die Aufnahme bleibt erhalten."));
            }
            MeetingId = record.Id;
            StatusMessage = notice;
            Phase = MeetingPhase.Idle;
            Log.Info($"Meeting evaluated: {record.Turns.Count} paragraphs");
            Finished?.Invoke(this, record);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            StatusMessage = null;
            MeetingId = meetingId;
            Phase = MeetingPhase.Idle;
            Log.Info("Meeting evaluation cancelled");
        }
        catch (Exception ex)
        {
            Log.Warn("Meeting evaluation failed", ex);
            ErrorMessage = ex is MeetingTranscriptionException or TranscriptionException ? ex.Message : L.F("Die Auswertung ist fehlgeschlagen: {0}", ex.Message);
            StatusMessage = notice;
            MeetingId = meetingId; // pending, with its audio: it can be evaluated again
            Phase = MeetingPhase.Idle;
            Finished?.Invoke(this, null);
        }
        finally
        {
            if (_processing == cancel) _processing = null;
            RuntimeNative.KeepAwake(false);
        }
    }

    private static string? Join(string? first, string? second) =>
        string.IsNullOrEmpty(first) ? second : string.IsNullOrEmpty(second) ? first : first + " " + second;
}
