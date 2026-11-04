namespace Diktat.Transcription.Meeting;

/// A paragraph of a meeting transcript. Speaker 0 is the person at this PC ("Ich"),
/// 1, 2, ... are the others ("Person 1", "Person 2").
public sealed record MeetingSegment(int Speaker, double Start, double End, string Text)
{
    public string SpeakerLabel => Label(Speaker);

    /// "Ich" for 0, "Person n" otherwise, as on the Mac (renamed people are the app's business).
    public static string Label(int speaker) => speaker == 0 ? "Ich" : $"Person {speaker}";
}

/// <param name="Notice">A German hint that something was left out, e.g. the system sound, or null.</param>
public sealed record MeetingTranscript(IReadOnlyList<MeetingSegment> Segments, string? Notice)
{
    /// Distinct people besides "Ich".
    public int OtherSpeakerCount => Segments.Where(s => s.Speaker > 0).Select(s => s.Speaker).Distinct().Count();
}

/// A meeting could not be evaluated, with a German message. The app keeps the audio, so the
/// evaluation can be repeated.
public sealed class MeetingTranscriptionException(string message, Exception? inner = null) : Exception(message, inner);

/// Turns a meeting recording into speaker-labelled paragraphs, like the macOS MeetingController:
///
/// - Online meeting (microphone plus the PC's sound, captured by WASAPI loopback): the
///   microphone is "Ich", without what it heard from the speakers (<see cref="EchoFilter"/>);
///   the system sound is diarized into "Person 1", "Person 2", ...
/// - Meeting in a room (microphone only): the microphone is diarized.
///
/// Each track is transcribed as a whole with word times and the words are then placed with
/// the speaker turns; transcribing short turns one by one made the language unstable.
public sealed class MeetingTranscriber
{
    private readonly Func<CancellationToken, Task<ISpeechTranscriber>> _transcriber;
    private readonly Func<ISpeakerDiarizer?> _diarizer;

    /// With the dictation model of the service and the diarizer the app has (null when the
    /// "Sprecher-Erkennung" package is not installed; then everyone is "Person 1").
    public MeetingTranscriber(LocalTranscriptionService service, Func<ISpeakerDiarizer?> diarizer)
        : this(ct => service.GetTranscriberAsync(ct), diarizer)
    {
    }

    public MeetingTranscriber(ISpeechTranscriber transcriber, ISpeakerDiarizer? diarizer)
        : this(_ => Task.FromResult(transcriber), () => diarizer)
    {
    }

    public MeetingTranscriber(Func<CancellationToken, Task<ISpeechTranscriber>> transcriber, Func<ISpeakerDiarizer?> diarizer)
    {
        _transcriber = transcriber;
        _diarizer = diarizer;
    }

    /// <param name="microphone">The microphone track, 16 kHz mono.</param>
    /// <param name="systemAudio">The PC's sound in an online meeting (16 kHz mono, same start), or null.</param>
    /// <param name="status">German status lines for the UI ("Stimmen werden erkannt ...").</param>
    public async Task<MeetingTranscript> TranscribeAsync(
        float[] microphone,
        float[]? systemAudio,
        string language,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(microphone);
        string? notice = null;

        status?.Report("Mikrofon wird transkribiert ...");
        var transcriber = await _transcriber(ct).ConfigureAwait(false);
        var words = (await transcriber.TranscribeAsync(microphone, language, ct).ConfigureAwait(false)).Words;
        ct.ThrowIfCancellationRequested();

        // An online meeting: the system sound holds the other people.
        IReadOnlyList<TimedWord>? systemWords = null;
        if (systemAudio is { Length: > 0 })
        {
            status?.Report("Ton des PCs wird transkribiert ...");
            try
            {
                var result = await transcriber.TranscribeAsync(systemAudio, language, ct).ConfigureAwait(false);
                // No speech there (e.g. cut short by a crash): the microphone is the whole meeting.
                if (result.Words.Count > 0) systemWords = result.Words;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                notice = $"Der Ton des PCs ließ sich nicht auswerten, ausgewertet wird nur das Mikrofon. ({error.Message})";
            }
            ct.ThrowIfCancellationRequested();
        }

        IReadOnlyList<SpeakerTurn> turns = [];
        var diarizer = _diarizer();
        if (diarizer is null)
        {
            notice = Join(notice, "Ohne Sprecher-Erkennung sind die Stimmen nicht getrennt. Sie lässt sich in den Einstellungen unter Modelle laden.");
        }
        else
        {
            status?.Report("Stimmen werden erkannt ...");
            try
            {
                turns = await diarizer.DiarizeAsync(systemWords is not null ? systemAudio! : microphone, ct: ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new MeetingTranscriptionException($"Die Stimmen ließen sich nicht erkennen. ({error.Message})", error);
            }
            ct.ThrowIfCancellationRequested();
        }

        var paragraphs = Paragraphs(words, systemWords, turns);
        var segments = paragraphs
            .Select(p => new MeetingSegment(p.Speaker, p.Start, p.End, p.Text.Trim()))
            .Where(s => s.Text.Length > 0)
            .ToList();
        if (segments.Count == 0)
        {
            throw new MeetingTranscriptionException("In der Aufnahme wurde keine Sprache erkannt.");
        }
        return new MeetingTranscript(segments, notice);
    }

    /// The paragraphs of both tracks in time order (pure logic, see the class comment).
    public static List<SpeakerAlignment.Paragraph> Paragraphs(IReadOnlyList<TimedWord> microphoneWords, IReadOnlyList<TimedWord>? systemWords, IReadOnlyList<SpeakerTurn> turns)
    {
        if (systemWords is null)
        {
            return SpeakerAlignment.Paragraphs(microphoneWords, turns);
        }
        // The microphone is "Ich", without what it heard from the speakers.
        var own = SpeakerAlignment.Paragraphs(EchoFilter.RemoveEcho(microphoneWords, systemWords), [])
            .Select(p => p with { Speaker = 0 })
            .ToList();
        var others = SpeakerAlignment.Paragraphs(systemWords, turns, own.Select(p => p.Start));
        return others.Concat(own).OrderBy(p => p.Start).ToList();
    }

    private static string Join(string? first, string second) => first is null ? second : first + " " + second;
}
