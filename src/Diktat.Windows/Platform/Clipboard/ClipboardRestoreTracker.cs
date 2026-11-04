namespace Diktat.Windows.Platform;

/// How long a paste keeps the transcript on the clipboard (port of the Mac timings, shorter:
/// Windows apps read the clipboard synchronously while handling Ctrl+V).
///
/// Restoring too early makes the target paste the old clipboard; restoring late costs almost
/// nothing. So every value leans towards waiting longer.
public sealed record ClipboardRestoreTimings
{
    /// Earliest restore after Ctrl+V, even if a read came in at once (a clipboard tool may have
    /// been first; some apps read twice).
    public TimeSpan MinimumHold { get; init; } = TimeSpan.FromMilliseconds(500);
    /// Quiet time after the last read.
    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromMilliseconds(300);
    /// Without any read the paste most likely went nowhere (no text field had focus, or the
    /// target runs elevated and Windows dropped our Ctrl+V).
    public TimeSpan NoReadTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// Hard upper bound, so a reader that never stops cannot hold the clipboard forever.
    public TimeSpan MaximumWait { get; init; } = TimeSpan.FromSeconds(10);
    /// When nothing read the transcript, leave it on the clipboard (as plain text, so the user
    /// can paste it by hand) instead of restoring the old content.
    public bool KeepTranscriptWhenUnread { get; init; } = true;

    public static ClipboardRestoreTimings Standard { get; } = new();
}

/// Pure bookkeeping for one paste: restore now, keep waiting, or leave the clipboard alone.
/// Times are monotonic seconds passed in by the caller, so tests control the clock.
internal struct ClipboardRestoreTracker
{
    public enum Decision { Wait, Restore, Skip, Unread }

    public double PastedAt { get; }
    public uint OwnSequence { get; set; }
    public ClipboardRestoreTimings Timings { get; }
    public int ReadCount { get; private set; }
    public double? FirstReadAt { get; private set; }
    public double? LastReadAt { get; private set; }
    /// Someone else emptied the clipboard (WM_DESTROYCLIPBOARD): their content wins.
    public bool Replaced { get; set; }

    public ClipboardRestoreTracker(double pastedAt, uint ownSequence, ClipboardRestoreTimings timings)
    {
        PastedAt = pastedAt;
        OwnSequence = ownSequence;
        Timings = timings;
    }

    public void RecordRead(double time)
    {
        ReadCount++;
        FirstReadAt = Math.Min(FirstReadAt ?? time, time);
        LastReadAt = Math.Max(LastReadAt ?? time, time);
    }

    /// When the snapshot goes back if nobody else touches the clipboard.
    public double RestoreAt
    {
        get
        {
            double due = LastReadAt is double last
                ? Math.Max(last + Timings.QuietPeriod.TotalSeconds, PastedAt + Timings.MinimumHold.TotalSeconds)
                : PastedAt + Timings.NoReadTimeout.TotalSeconds;
            return Math.Min(due, PastedAt + Timings.MaximumWait.TotalSeconds);
        }
    }

    public Decision Decide(double now, uint sequence, out double waitUntil)
    {
        waitUntil = 0;
        if (Replaced || sequence != OwnSequence) return Decision.Skip;
        double due = RestoreAt;
        if (now < due)
        {
            waitUntil = due;
            return Decision.Wait;
        }
        return ReadCount == 0 && Timings.KeepTranscriptWhenUnread ? Decision.Unread : Decision.Restore;
    }

    /// When the wait has to end now (new paste, exit): never Wait.
    public Decision Settle(uint sequence) => Replaced || sequence != OwnSequence ? Decision.Skip : Decision.Restore;
}
