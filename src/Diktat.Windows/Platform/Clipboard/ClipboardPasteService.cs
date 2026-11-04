using System.Diagnostics;
using Diktat.Windows.Platform.Native;

namespace Diktat.Windows.Platform;

/// Where a dictation should go: the foreground window and its focused control when the
/// recording started. Capture it with <see cref="Capture"/> at recording start, optionally add
/// the UI Automation focus (<see cref="FocusedFieldReader.CaptureFocusSpotAsync"/>) for apps that
/// draw all fields in one window (browsers, Office, Electron).
public sealed record PasteTarget(IntPtr Window, int ProcessId, string ExeName, IntPtr FocusWindow)
{
    public FocusSpot? Focus { get; init; }

    /// The current foreground window, or null when it belongs to Diktat or there is none.
    public static PasteTarget? Capture()
    {
        var app = ForegroundAppInfo.Current();
        if (app is null || app.IsOwnProcess) return null;
        return new PasteTarget(app.Window, app.ProcessId, app.ExeName, ForegroundAppInfo.FocusedWindow(app.Window));
    }
}

public enum PasteOutcome
{
    /// Ctrl+V was sent to the target.
    Pasted,
    /// The focus moved to another window or field: the text is on the clipboard instead.
    CopiedFocusChanged,
    /// No usable target (Diktat's own window in front, nothing focused): text on the clipboard.
    CopiedNoTarget,
    /// The target runs as administrator; Windows blocks our Ctrl+V. Text on the clipboard.
    CopiedTargetElevated,
    /// The clipboard stayed locked by another app; nothing changed.
    Failed,
}

public sealed record PasteResult(PasteOutcome Outcome)
{
    public bool TextIsOnClipboard => Outcome is PasteOutcome.CopiedFocusChanged or PasteOutcome.CopiedNoTarget or PasteOutcome.CopiedTargetElevated;
}

/// What happened to the clipboard after a paste.
public enum ClipboardSettlement
{
    /// The previous clipboard content is back.
    Restored,
    /// Someone else wrote to the clipboard meanwhile; their content stays.
    KeptNewer,
    /// Nothing read the transcript; it stays on the clipboard for a manual paste.
    KeptTranscript,
    /// The previous content could not be read or written back.
    RestoreFailed,
}

/// Pastes text at the cursor of the foreground app via the clipboard and Ctrl+V, and puts back
/// what the user had copied (port of the Mac ClipboardPasteService).
///
/// How: snapshot every clipboard format, offer the transcript with delayed rendering (the
/// first WM_RENDERFORMAT is the signal that the target read it), excluded from clipboard
/// history, send Ctrl+V once the user has let go of the shortcut keys, and restore the snapshot
/// once reads have been quiet (<see cref="ClipboardRestoreTimings"/>) — only if the clipboard
/// still holds our transcript (sequence number unchanged).
///
/// Threading: all clipboard work runs on a private STA thread that owns a message-only window
/// and keeps pumping (other apps send it WM_RENDERFORMAT synchronously). Public methods are
/// thread-safe and async. <see cref="Settled"/> is raised on the event context given to the
/// constructor (or a private background thread).
public sealed class ClipboardPasteService : IDisposable
{
    private const string ExcludeFromMonitors = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloud = "CanUploadToCloudClipboard";
    private const int OpenAttempts = 10;
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(20);

    private readonly MessageWindowThread _thread;
    private readonly EventDispatcher _events;
    private readonly SemaphoreSlim _pasteGate = new(1, 1);

    // Clipboard-thread state.
    private PendingPaste? _pending;
    private bool _restoring;
    private int _lastId;

    private sealed class PendingPaste
    {
        public required int Id;
        public required string Text;
        public required ClipboardSnapshot Snapshot;
        public ClipboardRestoreTracker Tracker;
        public IDisposable? Check;
    }

    public ClipboardRestoreTimings Timings { get; set; } = ClipboardRestoreTimings.Standard;

    /// After a paste with restore, once the clipboard question is decided.
    public event EventHandler<ClipboardSettlement>? Settled;

    public ClipboardPasteService(SynchronizationContext? eventContext = null)
    {
        _events = new EventDispatcher(eventContext);
        _thread = new MessageWindowThread("Diktat.Clipboard", HandleMessage);
    }

    /// Pastes <paramref name="text"/> into <paramref name="target"/> (captured when recording
    /// started; null = whatever has focus now). With <paramref name="restoreClipboard"/> the
    /// previous clipboard comes back afterwards. If the focus moved elsewhere, the text is only
    /// copied, and the result says so.
    public async Task<PasteResult> PasteAsync(string text, PasteTarget? target, bool restoreClipboard, CancellationToken cancellationToken = default)
    {
        await _pasteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The user may still hold Ctrl/Win of the shortcut: Ctrl+V with Win held is Win+V.
            await KeyboardInput.WaitForModifiersReleasedAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

            var outcome = await CheckTargetAsync(target, cancellationToken).ConfigureAwait(false);
            if (outcome != PasteOutcome.Pasted)
            {
                bool copied = await CopyCoreAsync(text).ConfigureAwait(false);
                return new PasteResult(copied ? outcome : PasteOutcome.Failed);
            }

            bool ready = restoreClipboard
                ? await RetryAsync(() => PrepareRestoringPaste(text)).ConfigureAwait(false)
                : await CopyCoreAsync(text).ConfigureAwait(false);
            if (!ready) return new PasteResult(PasteOutcome.Failed);

            if (!KeyboardInput.SendPaste()) Log.Warn("SendInput for Ctrl+V was not fully accepted");
            // The tracker's clock started when the transcript went on the clipboard, just before
            // Ctrl+V; reads (WM_RENDERFORMAT) may already have arrived and are kept.
            await _thread.InvokeAsync(Evaluate).ConfigureAwait(false);
            return new PasteResult(PasteOutcome.Pasted);
        }
        finally
        {
            _pasteGate.Release();
        }
    }

    /// Puts <paramref name="text"/> on the clipboard (plain, visible in clipboard history).
    public async Task<bool> CopyOnlyAsync(string text)
    {
        await _pasteGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await CopyCoreAsync(text).ConfigureAwait(false);
        }
        finally
        {
            _pasteGate.Release();
        }
    }

    /// Ends a waiting paste right away: restores the previous clipboard if it still holds our
    /// transcript. Call on app exit.
    public void SettleNow()
    {
        try
        {
            _thread.InvokeAsync(() => Settle("settled early")).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Log.Warn("Settling the clipboard failed", ex);
        }
    }

    // ---------------------------------------------------------------- target

    private static async Task<PasteOutcome> CheckTargetAsync(PasteTarget? target, CancellationToken cancellationToken)
    {
        var current = ForegroundAppInfo.Current();
        if (current is null) return target is null ? PasteOutcome.CopiedNoTarget : PasteOutcome.CopiedFocusChanged;

        if (current.IsOwnProcess)
        {
            // Our own window came to the front (tray window): bring the target back if it exists.
            if (target is null || !Win32.IsWindow(target.Window) || Win32.IsIconic(target.Window)) return PasteOutcome.CopiedNoTarget;
            Win32.SetForegroundWindow(target.Window);
            var deadline = Environment.TickCount64 + 300;
            while (Win32.GetAncestor(Win32.GetForegroundWindow(), Win32.GA_ROOT) != target.Window)
            {
                if (Environment.TickCount64 > deadline) return PasteOutcome.CopiedNoTarget;
                await Task.Delay(15, cancellationToken).ConfigureAwait(false);
            }
            current = ForegroundAppInfo.Current();
            if (current is null) return PasteOutcome.CopiedNoTarget;
        }

        if (target is not null)
        {
            if (current.Window != target.Window || current.ProcessId != target.ProcessId) return PasteOutcome.CopiedFocusChanged;
            var focus = ForegroundAppInfo.FocusedWindow(current.Window);
            if (target.FocusWindow != IntPtr.Zero && focus != IntPtr.Zero && focus != target.FocusWindow)
                return PasteOutcome.CopiedFocusChanged;
            if (target.Focus is { } before)
            {
                var now = await FocusedFieldReader.CaptureFocusSpotAsync().ConfigureAwait(false);
                // Unknown (UIA timed out) counts as unchanged: better paste than refuse.
                if (now is not null && !before.IsSameField(now)) return PasteOutcome.CopiedFocusChanged;
            }
        }

        if (ForegroundAppInfo.IsElevated(current.ProcessId) == true && !ForegroundAppInfo.IsCurrentProcessElevated)
            return PasteOutcome.CopiedTargetElevated;
        return PasteOutcome.Pasted;
    }

    // ---------------------------------------------------------------- clipboard thread

    private Task<bool> CopyCoreAsync(string text) => RetryAsync(() =>
    {
        if (!Win32.OpenClipboard(_thread.Handle)) return null;
        try
        {
            DropPending();
            _restoring = true;
            Win32.EmptyClipboard();
            _restoring = false;
            return ClipboardSnapshot.SetUnicodeText(text);
        }
        finally
        {
            _restoring = false;
            Win32.CloseClipboard();
        }
    });

    /// Snapshot + transcript with delayed rendering. Null = clipboard locked (retry).
    private bool? PrepareRestoringPaste(string text)
    {
        Settle("new paste");
        if (!Win32.OpenClipboard(_thread.Handle)) return null;
        try
        {
            var snapshot = ClipboardSnapshot.ReadOpenClipboard();
            _restoring = true;
            Win32.EmptyClipboard();
            _restoring = false;
            if (snapshot is null)
            {
                Log.Info("Previous clipboard unreadable; the transcript stays on it");
                return ClipboardSnapshot.SetUnicodeText(text);
            }

            // Keeps the one-off transcript out of Win+V history and cloud clipboard.
            ClipboardSnapshot.SetDword(ExcludeFromMonitors, 1);
            ClipboardSnapshot.SetDword(CanIncludeInHistory, 0);
            ClipboardSnapshot.SetDword(CanUploadToCloud, 0);
            Win32.SetClipboardData(Win32.CF_UNICODETEXT, IntPtr.Zero); // delayed: rendered on WM_RENDERFORMAT
            bool delayed = Win32.IsClipboardFormatAvailable(Win32.CF_UNICODETEXT);
            if (!delayed && !ClipboardSnapshot.SetUnicodeText(text)) return false;

            _pending = new PendingPaste
            {
                Id = ++_lastId,
                Text = text,
                Snapshot = snapshot,
            };
            return true;
        }
        finally
        {
            _restoring = false;
            Win32.CloseClipboard();
            // Sequence after our own writes; anything later is someone else.
            if (_pending is { } pending && pending.Tracker.PastedAt == 0)
            {
                pending.Tracker = new ClipboardRestoreTracker(Now(), Win32.GetClipboardSequenceNumber(), Timings);
            }
        }
    }

    private IntPtr? HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_RENDERFORMAT:
                if ((uint)wParam == Win32.CF_UNICODETEXT && _pending is { } pending)
                {
                    // The clipboard is open by the reader; only SetClipboardData is allowed here.
                    ClipboardSnapshot.SetUnicodeText(pending.Text);
                    pending.Tracker.OwnSequence = Win32.GetClipboardSequenceNumber();
                    pending.Tracker.RecordRead(Now());
                    // Evaluate after returning, never inside the reader's open clipboard.
                    pending.Check?.Dispose();
                    pending.Check = _thread.Schedule(TimeSpan.Zero, Evaluate);
                }
                return IntPtr.Zero;

            case Win32.WM_RENDERALLFORMATS:
            case Win32.WM_DESTROY:
                // Diktat quits while the transcript is only promised: render it so it is not lost.
                if (_pending is { } owned && Win32.OpenClipboard(hwnd))
                {
                    try
                    {
                        if (Win32.GetClipboardOwner() == hwnd) ClipboardSnapshot.SetUnicodeText(owned.Text);
                    }
                    finally
                    {
                        Win32.CloseClipboard();
                    }
                }
                return msg == Win32.WM_DESTROY ? null : IntPtr.Zero;

            case Win32.WM_DESTROYCLIPBOARD:
                if (!_restoring && _pending is { } replaced) replaced.Tracker.Replaced = true;
                return IntPtr.Zero;
        }
        return null;
    }

    private void Evaluate()
    {
        if (_pending is not { } pending) return;
        pending.Check?.Dispose();
        pending.Check = null;
        var decision = pending.Tracker.Decide(Now(), CurrentSequence(pending), out var until);
        if (decision == ClipboardRestoreTracker.Decision.Wait)
        {
            pending.Check = _thread.Schedule(TimeSpan.FromSeconds(Math.Max(0.01, until - Now())), Evaluate);
            return;
        }
        Finish(pending, decision, trigger: null);
    }

    private void Settle(string trigger)
    {
        if (_pending is not { } pending) return;
        pending.Check?.Dispose();
        pending.Check = null;
        Finish(pending, pending.Tracker.Settle(CurrentSequence(pending)), trigger);
    }

    /// The clipboard counts as unchanged while our window still owns it: anyone else writing to
    /// it must empty it first, which takes the ownership away and sends us WM_DESTROYCLIPBOARD
    /// (Replaced). The sequence number alone is not reliable here, because Windows may count
    /// the delayed rendering of the transcript as a change after OwnSequence was read.
    private uint CurrentSequence(PendingPaste pending) =>
        Win32.GetClipboardOwner() == _thread.Handle && !pending.Tracker.Replaced
            ? pending.Tracker.OwnSequence
            : Win32.GetClipboardSequenceNumber();

    private void DropPending()
    {
        if (_pending is { } pending) pending.Check?.Dispose();
        _pending = null;
    }

    private void Finish(PendingPaste paste, ClipboardRestoreTracker.Decision decision, string? trigger)
    {
        _pending = null;
        var tracker = paste.Tracker;
        int elapsed = (int)((Now() - tracker.PastedAt) * 1000);
        ClipboardSettlement settlement;

        if (decision == ClipboardRestoreTracker.Decision.Skip)
        {
            Log.Info($"Clipboard changed while waiting; keeping the newer content ({elapsed} ms)");
            settlement = ClipboardSettlement.KeptNewer;
        }
        else if (decision == ClipboardRestoreTracker.Decision.Unread)
        {
            // Make the transcript a normal clipboard entry (no promise, visible in history).
            bool ok = TryWithOpenClipboard(() =>
            {
                _restoring = true;
                Win32.EmptyClipboard();
                _restoring = false;
                return ClipboardSnapshot.SetUnicodeText(paste.Text);
            });
            Log.Info($"Nothing read the transcript within {Timings.NoReadTimeout.TotalSeconds:0.#} s; it stays on the clipboard");
            settlement = ok ? ClipboardSettlement.KeptTranscript : ClipboardSettlement.RestoreFailed;
        }
        else
        {
            bool ok = TryWithOpenClipboard(() =>
            {
                _restoring = true;
                Win32.EmptyClipboard();
                _restoring = false;
                paste.Snapshot.WriteToOpenClipboard();
                // The restored content was in the history already; do not add it twice.
                ClipboardSnapshot.SetDword(CanIncludeInHistory, 0);
                ClipboardSnapshot.SetDword(CanUploadToCloud, 0);
                return true;
            });
            if (ok)
            {
                Log.Info(tracker.FirstReadAt is double first
                    ? $"Restored the previous clipboard after {elapsed} ms; {tracker.ReadCount} reads, first after {(int)((first - tracker.PastedAt) * 1000)} ms"
                    : $"Restored the previous clipboard after {elapsed} ms without a read ({trigger ?? "timeout"})");
            }
            else
            {
                Log.Warn("Could not write the previous clipboard back");
            }
            settlement = ok ? ClipboardSettlement.Restored : ClipboardSettlement.RestoreFailed;
        }
        _events.Post(() => Settled?.Invoke(this, settlement));
    }

    /// Opens the clipboard with retries (it can be locked by another app for a moment). Runs on
    /// the clipboard thread and blocks it for at most ~200 ms; only used where waiting
    /// asynchronously is not possible (restore from a timer).
    private bool TryWithOpenClipboard(Func<bool> action)
    {
        for (int attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (Win32.OpenClipboard(_thread.Handle))
            {
                try
                {
                    return action();
                }
                finally
                {
                    _restoring = false;
                    Win32.CloseClipboard();
                }
            }
            Thread.Sleep(OpenRetryDelay);
        }
        return false;
    }

    /// Runs <paramref name="attempt"/> on the clipboard thread until it returns non-null,
    /// waiting between attempts without blocking that thread (so it keeps rendering for others).
    private async Task<bool> RetryAsync(Func<bool?> attempt)
    {
        for (int i = 0; i < OpenAttempts; i++)
        {
            var result = await _thread.InvokeAsync(attempt).ConfigureAwait(false);
            if (result is bool done) return done;
            await Task.Delay(OpenRetryDelay).ConfigureAwait(false);
        }
        Log.Warn("Clipboard stayed locked by another app");
        return false;
    }

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public void Dispose()
    {
        SettleNow();
        _thread.Dispose();
        _events.Dispose();
        _pasteGate.Dispose();
    }
}
