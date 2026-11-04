namespace Diktat.Transcription.Meeting;

/// A stretch of the recording in which one speaker talks. Speakers are numbered 1, 2, ... by
/// first appearance; 0 is the person at this PC ("Ich") in an online meeting.
public readonly record struct SpeakerTurn(int Speaker, double Start, double End);

/// Assigns the words of one transcript to speaker turns by time (port of the macOS
/// SpeakerAlignment). Transcribing the whole meeting at once keeps the language stable;
/// transcribing short turns one by one made Parakeet misdetect German as English.
public static class SpeakerAlignment
{
    public readonly record struct Paragraph(int Speaker, double Start, double End, string Text);

    /// Without speaker turns, paragraphs break at pauses longer than this.
    public const double ParagraphPause = 1.5;

    /// <param name="breaks">Where someone on another track starts talking, e.g. the microphone in
    /// an online meeting. A paragraph spanning such a moment is split there, so a one-on-one call
    /// reads as a conversation and not as one block per person.</param>
    public static List<Paragraph> Paragraphs(IReadOnlyList<TimedWord> words, IReadOnlyList<SpeakerTurn> turns, IEnumerable<double>? breaks = null)
    {
        var paragraphs = new List<Paragraph>();
        var previousStart = double.NegativeInfinity;
        var previousEnd = 0.0;
        var sortedBreaks = (breaks ?? []).OrderBy(b => b).ToList();
        var nextBreak = 0;

        foreach (var word in words)
        {
            var speaker = turns.Count == 0 ? 1 : SpeakerAt((word.Start + word.End) / 2, turns);
            var pauseBreak = turns.Count == 0 && word.Start - previousEnd > ParagraphPause;
            var otherTrackBreak = false;
            while (nextBreak < sortedBreaks.Count && sortedBreaks[nextBreak] <= word.Start)
            {
                otherTrackBreak = otherTrackBreak || sortedBreaks[nextBreak] > previousStart;
                nextBreak++;
            }
            if (paragraphs.Count > 0 && paragraphs[^1].Speaker == speaker && !pauseBreak && !otherTrackBreak)
            {
                var last = paragraphs[^1];
                paragraphs[^1] = last with { End = Math.Max(last.End, word.End), Text = last.Text + " " + word.Text };
            }
            else
            {
                paragraphs.Add(new Paragraph(speaker, word.Start, word.End, word.Text));
            }
            previousStart = word.Start;
            previousEnd = word.End;
        }
        return paragraphs;
    }

    /// The turn containing <paramref name="time"/>, otherwise the closest one.
    private static int SpeakerAt(double time, IReadOnlyList<SpeakerTurn> turns)
    {
        foreach (var turn in turns)
        {
            if (turn.Start <= time && time <= turn.End) return turn.Speaker;
        }
        var best = turns[0];
        var bestDistance = double.MaxValue;
        foreach (var turn in turns)
        {
            var distance = time < turn.Start ? turn.Start - time : time - turn.End;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = turn;
            }
        }
        return best.Speaker;
    }

    /// Speaker turns in time order from raw diarizer segments: speakers are renumbered by first
    /// appearance (1, 2, ...), consecutive segments of one speaker less than a second apart are
    /// merged, and turns shorter than 0.4 s are dropped (as on the Mac).
    public static List<SpeakerTurn> NormalizeTurns(IEnumerable<(double Start, double End, int SpeakerId)> segments)
    {
        var numbers = new Dictionary<int, int>();
        var turns = new List<SpeakerTurn>();
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            if (!numbers.TryGetValue(segment.SpeakerId, out var number))
            {
                number = numbers.Count + 1;
                numbers[segment.SpeakerId] = number;
            }
            if (turns.Count > 0 && turns[^1].Speaker == number && segment.Start - turns[^1].End < 1.0)
            {
                turns[^1] = turns[^1] with { End = Math.Max(turns[^1].End, segment.End) };
            }
            else
            {
                turns.Add(new SpeakerTurn(number, segment.Start, segment.End));
            }
        }
        var kept = turns.Where(t => t.End - t.Start >= 0.4).ToList();
        // A speaker heard only in dropped scraps leaves no gap: Person 1, 2, 3 stay consecutive.
        var renumbered = new Dictionary<int, int>();
        return kept.Select(t =>
        {
            if (!renumbered.TryGetValue(t.Speaker, out var n)) renumbered[t.Speaker] = n = renumbered.Count + 1;
            return t with { Speaker = n };
        }).ToList();
    }
}
