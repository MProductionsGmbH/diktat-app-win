namespace Diktat.AI.Meetings;

/// "Who spoke when" for meeting recordings, on this PC. The model behind it (pyannote
/// segmentation, speaker embeddings, clustering) lives in the transcription project; this is
/// the contract the meeting logic needs.
public interface ISpeakerDiarizer
{
    /// Loads the models, downloading them the first time.
    Task PrepareAsync(CancellationToken cancellationToken = default);

    /// Speaker turns in time order, numbered by first appearance (1, 2, ...). Implementations
    /// can build them from raw segments with <see cref="SpeakerTurns.FromSegments"/>.
    Task<IReadOnlyList<SpeakerTurn>> TurnsAsync(string audioPath, CancellationToken cancellationToken = default);
}

/// One raw segment from a diarization model.
public readonly record struct DiarizationSegment(string SpeakerId, double Start, double End);

/// The speech model that turns a meeting into words with times (Parakeet or Whisper on the PC).
public interface IMeetingWordTranscriber
{
    Task<IReadOnlyList<TimedWord>> WordsAsync(string audioPath, string language, CancellationToken cancellationToken = default);
}

public static class SpeakerTurns
{
    /// Turns shorter than this are dropped.
    public const double MinimumTurn = 0.4;
    /// Consecutive segments of the same speaker closer than this are merged.
    public const double MergeGap = 1.0;

    /// Sorts the segments, merges consecutive segments of the same speaker and numbers the
    /// speakers by first appearance (1, 2, ...), as SpeakerDiarizer.swift does.
    public static List<SpeakerTurn> FromSegments(IEnumerable<DiarizationSegment> segments)
    {
        var numbers = new Dictionary<string, int>();
        var turns = new List<SpeakerTurn>();
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            if (!numbers.TryGetValue(segment.SpeakerId, out var number))
            {
                number = numbers.Count + 1;
                numbers[segment.SpeakerId] = number;
            }
            if (turns.Count > 0 && turns[^1].Speaker == number && segment.Start - turns[^1].End < MergeGap)
            {
                turns[^1] = new SpeakerTurn(number, turns[^1].Start, segment.End);
            }
            else
            {
                turns.Add(new SpeakerTurn(number, segment.Start, segment.End));
            }
        }
        return turns.Where(t => t.End - t.Start >= MinimumTurn).ToList();
    }
}
