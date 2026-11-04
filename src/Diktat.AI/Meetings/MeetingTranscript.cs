namespace Diktat.AI.Meetings;

/// Turns the words of a meeting into turns per person (the pure part of MeetingController on the Mac).
public static class MeetingTranscript
{
    /// <param name="microphoneWords">The words of the microphone track.</param>
    /// <param name="systemWords">The words of what the PC played in an online meeting, or null for
    /// a meeting in the room.</param>
    /// <param name="turns">Who spoke when: in the system sound for an online meeting, else in the microphone.</param>
    /// <param name="postProcess">The dictation post-processing (filler words, custom terms), or null.</param>
    public static List<MeetingTurn> BuildTurns(IReadOnlyList<TimedWord> microphoneWords, IReadOnlyList<TimedWord>? systemWords,
        IReadOnlyList<SpeakerTurn> turns, Func<string, string>? postProcess = null)
    {
        List<SpeakerAlignment.Paragraph> paragraphs;
        if (systemWords is not null)
        {
            // The microphone is "Ich", without what it heard from the speakers.
            var own = SpeakerAlignment.Paragraphs(EchoFilter.RemoveEcho(microphoneWords, systemWords), [])
                .Select(p => p with { Speaker = 0 })
                .ToList();
            var others = SpeakerAlignment.Paragraphs(systemWords, turns, own.Select(p => p.Start));
            paragraphs = others.Concat(own).OrderBy(p => p.Start).ToList();
        }
        else
        {
            paragraphs = SpeakerAlignment.Paragraphs(microphoneWords, turns);
        }

        var result = new List<MeetingTurn>();
        foreach (var paragraph in paragraphs)
        {
            var text = (postProcess?.Invoke(paragraph.Text) ?? paragraph.Text).Trim();
            if (text.Length > 0) result.Add(new MeetingTurn(paragraph.Speaker, paragraph.Start, text));
        }
        return result;
    }
}
