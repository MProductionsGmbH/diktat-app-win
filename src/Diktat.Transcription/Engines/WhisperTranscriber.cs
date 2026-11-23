using Diktat.Core.Text;
using Whisper.net;

namespace Diktat.Transcription.Engines;

/// OpenAI Whisper (ggml files of whisper.cpp) through Whisper.net. The factory (the loaded
/// weights) lives as long as this object; the processor is reused between calls and only
/// rebuilt when the language changes. One transcription at a time.
///
/// Mirrors the macOS WhisperDictation: "auto" detects the language (a fixed language makes
/// Whisper translate English speech into German), segments over room noise are dropped, and
/// stock phrases on silence ("Vielen Dank.", subtitle credits) count as no text.
///
/// A recording longer than one window of the model (30 seconds) is cut at pauses in the speech
/// (see <see cref="Pieces"/>), and its language is decided once for all pieces (see
/// <see cref="SpokenLanguage"/>).
public sealed class WhisperTranscriber : ISpeechTranscriber
{
    private const int SampleRate = 16_000;
    /// whisper.cpp skips input shorter than one second.
    private const int MinimumSamples = SampleRate * 6 / 5;

    /// One window of the model: the longest piece decoded at once.
    public const double WindowSeconds = 30;
    /// How far back from the window end a cut may move to find a pause.
    public const double CutSearchSeconds = 10;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _threads;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string? _processorLanguage;

    private WhisperTranscriber(string modelId, WhisperFactory factory, int threads)
    {
        ModelId = modelId;
        _factory = factory;
        _threads = threads;
    }

    public string ModelId { get; }

    /// Loads a ggml model file. Takes a few seconds; call it off the UI thread.
    public static WhisperTranscriber Load(string modelPath, string modelId, int? threads = null)
    {
        if (!File.Exists(modelPath)) throw new TranscriptionException(TranscriptionError.ModelIncomplete, TranscriptionException.IncompleteMessage(modelId));
        var factory = WhisperFactory.FromPath(modelPath);
        var transcriber = new WhisperTranscriber(modelId, factory, threads ?? ParakeetTranscriber.DefaultThreads());
        // Builds the processor (and its buffers) now, so the first dictation starts at once.
        transcriber.EnsureProcessor(TranscriptionLanguage.Auto);
        return transcriber;
    }

    public async Task<TranscriptionResult> TranscribeAsync(float[] samples16kMono, string language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(samples16kMono);
        if (samples16kMono.Length == 0) return TranscriptionResult.Empty;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var picked = TranscriptionLanguage.Code(language);
            var samples = samples16kMono.Length >= MinimumSamples ? samples16kMono : Padded(samples16kMono);
            var pieces = Pieces(samples);
            // Any loaded processor can tell the language; only the decoding needs the right one.
            var detector = _processor ?? EnsureProcessor(picked ?? TranscriptionLanguage.Auto);
            var spoken = await Task.Run(() => SpokenLanguage(picked, samples, pieces.Count, detector.DetectLanguage), ct).ConfigureAwait(false);
            var processor = EnsureProcessor(spoken ?? TranscriptionLanguage.Auto);

            var segments = new List<SegmentData>();
            var offsets = new List<double>();
            foreach (var piece in pieces)
            {
                ct.ThrowIfCancellationRequested();
                var audio = piece.Length == samples.Length ? samples : samples.AsSpan(piece.Start, piece.Length).ToArray();
                if (audio.Length < MinimumSamples) audio = Padded(audio);
                await foreach (var segment in processor.ProcessAsync(audio, ct).ConfigureAwait(false))
                {
                    segments.Add(segment);
                    offsets.Add(piece.StartSeconds);
                }
            }
            return BuildResult(segments, samples, spoken, offsets);
        }
        finally
        {
            _gate.Release();
        }
    }

    private WhisperProcessor EnsureProcessor(string language)
    {
        var factory = _factory ?? throw new ObjectDisposedException(nameof(WhisperTranscriber));
        if (_processor is not null && _processorLanguage == language) return _processor;
        _processor?.Dispose();
        _processor = null;

        var builder = factory.CreateBuilder()
            .WithThreads(_threads)
            .WithTokenTimestamps();
        builder = language == TranscriptionLanguage.Auto ? builder.WithLanguageDetection() : builder.WithLanguage(language);
        _processor = builder.Build();
        _processorLanguage = language;
        return _processor;
    }

    /// The pieces a recording is decoded in. Up to one window it is one piece; a longer one is cut
    /// at pauses in the speech and not wherever whisper.cpp's window ends. Without that, a window
    /// the model ends early takes the rest of its half minute along: in a four-minute dialect
    /// recording on the Mac a third of the text went missing.
    internal static List<AudioChunker.Chunk> Pieces(float[] samples) =>
        samples.Length > WindowSeconds * SampleRate
            ? AudioChunker.Split(samples, WindowSeconds, CutSearchSeconds, SampleRate)
            : [new AudioChunker.Chunk(0, samples.Length)];

    /// The language of a recording: the one picked in the settings, else what the model takes the
    /// start (up to one window) for, asked once and then held for every piece. Left to itself the
    /// model chooses anew in every piece, and with dialect that goes wrong: on the Mac a Swiss
    /// German recording came out partly as an English translation, partly in Cyrillic. Null when
    /// one piece is all (whisper.cpp then detects once by itself) or the model cannot say.
    internal static string? SpokenLanguage(string? picked, float[] samples, int pieceCount, Func<float[], string?> detect)
    {
        if (picked is not null) return picked;
        if (pieceCount <= 1) return null;
        var window = (int)(WindowSeconds * SampleRate);
        var start = samples.Length > window ? samples[..window] : samples;
        return TranscriptionLanguage.Code(detect(start));
    }

    private static float[] Padded(float[] samples)
    {
        var padded = new float[MinimumSamples];
        samples.CopyTo(padded, 0);
        return padded;
    }

    /// <param name="offsets">Per segment, where its piece starts in <paramref name="samples"/>, in
    /// seconds; null when the segments come from one piece.</param>
    internal static TranscriptionResult BuildResult(IReadOnlyList<SegmentData> segments, float[] samples, string? requestedLanguage,
        IReadOnlyList<double>? offsets = null)
    {
        double Offset(int i) => offsets is null ? 0 : offsets[i];
        var voiced = segments
            .Select((s, i) => HallucinationFilter.VoicedSeconds(samples, Offset(i) + s.Start.TotalSeconds, Offset(i) + s.End.TotalSeconds))
            .ToList();
        var flags = HallucinationFilter.KeepFlags(voiced);

        var text = string.Join(' ', segments.Select(s => s.Text.Trim()).Where(t => t.Length > 0)).Trim();
        if (flags.Any(keep => !keep))
        {
            text = string.Join(' ', segments.Where((_, i) => flags[i]).Select(s => HallucinationFilter.StrippingTokens(s.Text)).Where(t => t.Length > 0)).Trim();
        }
        text = HallucinationFilter.StrippingTokens(text);
        if (HallucinationFilter.IsInvented(text, HallucinationFilter.VoicedSeconds(samples)))
        {
            return TranscriptionResult.Empty;
        }
        text = HallucinationFilter.RemovingTrailingCredits(text);

        // Words with times; a quiet speaker far from the microphone must stay, so only short
        // segments over room noise are left out.
        var words = new List<TimedWord>();
        for (var i = 0; i < segments.Count; i++)
        {
            var segmentWords = SegmentWords(segments[i], Offset(i));
            if (!flags[i] && segmentWords.Count <= 3) continue;
            words.AddRange(segmentWords);
        }
        words.Sort((a, b) => a.Start.CompareTo(b.Start));

        var detected = segments.Select(s => s.Language).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && l != "auto");
        return new TranscriptionResult(text, detected ?? requestedLanguage, words);
    }

    /// Whisper tokens begin with a space at a word start; control tokens ("[_BEG_]", "<|de|>")
    /// carry no text. Without token times the segment's span is spread over its words. Times are
    /// shifted by <paramref name="offset"/>, the start of the segment's piece.
    private static List<TimedWord> SegmentWords(SegmentData segment, double offset)
    {
        var tokens = (segment.Tokens ?? [])
            .Where(t => !string.IsNullOrEmpty(t.Text) && !IsControl(t.Text))
            .Select(t => (t.Text!, Start: offset + t.Start / 100.0, End: offset + t.End / 100.0))
            .ToList();
        var segmentStart = offset + segment.Start.TotalSeconds;
        var segmentEnd = Math.Max(segmentStart, offset + segment.End.TotalSeconds);
        if (tokens.Count > 0 && tokens.All(t => t.End >= t.Start) && tokens.Any(t => t.End > offset))
        {
            return TimedWord.FromTokens(tokens)
                .Select(w => w with { Text = HallucinationFilter.StrippingTokens(w.Text) })
                .Where(w => w.Text.Length > 0)
                .ToList();
        }
        var plain = HallucinationFilter.StrippingTokens(segment.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var step = plain.Length > 0 ? (segmentEnd - segmentStart) / plain.Length : 0;
        return plain.Select((w, i) => new TimedWord(w, segmentStart + i * step, segmentStart + (i + 1) * step)).ToList();
    }

    private static bool IsControl(string text)
    {
        var trimmed = text.Trim();
        return (trimmed.StartsWith("[_") && trimmed.EndsWith(']')) || (trimmed.StartsWith("<|") && trimmed.EndsWith("|>"));
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _processor?.Dispose();
            _processor = null;
            _factory?.Dispose();
            _factory = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
