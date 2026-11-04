using Diktat.AI.Internal;

namespace Diktat.AI.Meetings;

/// A transcribed word with its position in the recording, in seconds.
public readonly record struct TimedWord(string Text, double Start, double End)
{
    /// Joins SentencePiece tokens into words: a token starting with a space or "▁" begins a new word.
    public static List<TimedWord> Words(IEnumerable<(string Text, double Start, double End)> tokens)
    {
        var words = new List<TimedWord>();
        foreach (var token in tokens)
        {
            var piece = token.Text.Replace('▁', ' ');
            var startsWord = piece.StartsWith(' ') || words.Count == 0;
            var trimmed = TextHelpers.Trim(piece, TextHelpers.IsHorizontalWhitespace);
            if (trimmed.Length == 0) continue;
            if (startsWord)
            {
                words.Add(new TimedWord(trimmed, token.Start, token.End));
            }
            else
            {
                var last = words[^1];
                words[^1] = new TimedWord(last.Text + trimmed, last.Start, token.End);
            }
        }
        return words;
    }
}

/// A stretch of the recording in which one speaker talks.
public readonly record struct SpeakerTurn(int Speaker, double Start, double End);

/// Assigns the words of one transcript to speaker turns by time.
///
/// Transcribing the whole meeting at once keeps the language stable; transcribing short turns
/// one by one made Parakeet misdetect German as English.
public static class SpeakerAlignment
{
    public readonly record struct Paragraph(int Speaker, double Start, string Text);

    /// Without speaker turns, paragraphs break at pauses longer than this.
    public const double ParagraphPause = 1.5;

    /// <param name="breaks">Where someone on another track starts talking, e.g. the microphone in an
    /// online meeting. A paragraph spanning such a moment is split there, so a one-on-one call reads
    /// as a conversation and not as one block per person.</param>
    public static List<Paragraph> Paragraphs(IReadOnlyList<TimedWord> words, IReadOnlyList<SpeakerTurn> turns,
        IEnumerable<double>? breaks = null)
    {
        var paragraphs = new List<Paragraph>();
        var previousStart = double.NegativeInfinity;
        var previousEnd = 0.0;
        var sortedBreaks = (breaks ?? []).OrderBy(b => b).ToList();
        var nextBreak = 0;

        foreach (var word in words)
        {
            var speaker = turns.Count == 0 ? 1 : SpeakerId((word.Start + word.End) / 2, turns);
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
                paragraphs[^1] = new Paragraph(speaker, last.Start, last.Text + " " + word.Text);
            }
            else
            {
                paragraphs.Add(new Paragraph(speaker, word.Start, word.Text));
            }
            previousStart = word.Start;
            previousEnd = word.End;
        }
        return paragraphs;
    }

    /// The turn containing the time, otherwise the closest one.
    private static int SpeakerId(double time, IReadOnlyList<SpeakerTurn> turns)
    {
        foreach (var turn in turns)
        {
            if (turn.Start <= time && time <= turn.End) return turn.Speaker;
        }
        SpeakerTurn? closest = null;
        foreach (var turn in turns)
        {
            if (closest is null || Distance(time, turn) < Distance(time, closest.Value)) closest = turn;
        }
        return closest?.Speaker ?? 1;
    }

    private static double Distance(double time, SpeakerTurn turn) => time < turn.Start ? turn.Start - time : time - turn.End;
}
