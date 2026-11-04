using System.ComponentModel;
using Diktat.AI.Updates;
using Diktat.Core.Settings;

namespace Diktat.Windows.Runtime;

// The contract between the runtime (Runtime/**: dictation, meetings, audio files, tray, pill)
// and the windows (Views/**: tray panel, main window, settings, setup assistant). Views only
// talk to the app through this interface and AppServices. Every member is used on the UI
// thread; PropertyChanged is raised there too.

public enum DictationPhase
{
    Idle,
    Recording,
    /// Transcribing or rewriting; <see cref="IDiktatApp.StatusMessage"/> says which.
    Processing,
    /// The text is ready (pasted or copied); shown briefly, then back to Idle.
    Done,
    Error,
}

public enum MeetingPhase
{
    Idle,
    Recording,
    /// Transcribing and summarizing after the recording; StatusMessage of the meeting says what.
    Processing,
}

/// The current or last meeting recording, for the meeting page.
public interface IMeetingSession : INotifyPropertyChanged
{
    MeetingPhase Phase { get; }
    /// True when the system audio (Teams, Zoom, Meet) is recorded too.
    bool IsOnline { get; }
    TimeSpan Elapsed { get; }
    float MicrophoneLevel { get; }
    float SystemAudioLevel { get; }
    string? StatusMessage { get; }
    /// Live notes (beta) while recording: topics, decisions, to-dos. Empty when off.
    string LiveNotes { get; }
    /// Id of the finished meeting in AppServices.Meetings, once it is saved.
    Guid? MeetingId { get; }
    string? ErrorMessage { get; }
}

public interface IDiktatApp : INotifyPropertyChanged
{
    AppServices Services { get; }

    // --- Dictation ---------------------------------------------------------------------

    DictationPhase Phase { get; }
    /// The mode that is recording or processing, or null.
    WorkflowType? ActiveMode { get; }
    /// "Aufnahme läuft ...", "Wird transkribiert ...", an error text, ...
    string? StatusMessage { get; }
    /// Input level 0..1 while recording, for the waveforms.
    float AudioLevel { get; }
    /// The microphone delivers only silence (muted, wrong device).
    bool NoSignal { get; }
    /// The last finished text, for "Letzter Text" in the tray panel.
    string? LastText { get; }
    /// What was said word for word before an AI mode rewrote it; null for plain dictation.
    string? LastTranscript { get; }
    /// The last text was copied instead of pasted (focus moved, no text field, admin window).
    bool LastTextWasOnlyCopied { get; }

    /// Whether the mode can run now (a speech model is there; AI modes need a provider).
    bool IsModeAvailable(WorkflowType mode);
    /// Why a mode is not available, in German, or null.
    string? ModeUnavailableReason(WorkflowType mode);

    /// Starts recording in a mode, as the shortcut does (the target is the window in front).
    void StartDictation(WorkflowType mode);
    /// Stops recording and processes the text.
    void StopDictation();
    /// Stops and throws the recording away.
    void CancelDictation();
    /// After an AI mode: replaces the pasted text with what was actually said.
    void InsertTranscriptInstead();

    void CopyToClipboard(string text);

    /// Applies changed shortcuts or the hold/toggle mode from the settings.
    void ReloadHotkeys();
    /// Records the next shortcut the user presses (settings), null on Escape.
    Task<KeyCombo?> CaptureShortcutAsync(CancellationToken cancellationToken);

    // --- Meetings ----------------------------------------------------------------------

    /// The running or last meeting session, or null before the first one.
    IMeetingSession? Meeting { get; }
    void StartMeeting(bool online);
    void StopMeeting();
    void CancelMeeting();
    /// Writes (again) the summary of a saved meeting.
    Task SummarizeMeetingAsync(Guid meetingId, CancellationToken cancellationToken);
    /// Whether the raw audio of a meeting is still on this PC: a meeting whose evaluation failed
    /// or was interrupted (status Pending) can then be evaluated again.
    bool HasMeetingAudio(Guid meetingId);
    /// Evaluates a pending meeting again from its audio; becomes the current <see cref="Meeting"/>.
    void RetryMeeting(Guid meetingId);

    // --- Audio files -------------------------------------------------------------------

    /// Transcribes a voice memo or WhatsApp voice message on this PC.
    Task<string> TranscribeAudioFileAsync(string path, bool paragraphs, IProgress<string>? status, CancellationToken cancellationToken);

    // --- Updates and windows -----------------------------------------------------------

    /// The newest release when it is newer than this version, else null.
    Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken);
    UpdateInfo? AvailableUpdate { get; }

    /// Opens the main window on a page: "history", "modes", "meetings", "settings" (or a
    /// settings section like "settings/models").
    void ShowMainWindow(string? page = null);
    void ShowSetupAssistant();
    void ShowTrayPanel();
    void Quit();
}
