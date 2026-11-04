using Diktat.Transcription.Models;
using SherpaOnnx;

namespace Diktat.Transcription.Engines;

/// NVIDIA Parakeet TDT 0.6B v3 (25 European languages, punctuation and capitals) through
/// sherpa-onnx on the CPU. The model detects the language itself; sherpa-onnx takes no
/// language hint for it, so <c>language</c> is ignored and no detected language is reported.
///
/// Long recordings are cut into pieces of at most a minute at the quietest moment near each
/// piece's end (see <see cref="AudioChunker"/>); the word times are shifted back accordingly.
public sealed class ParakeetTranscriber : ISpeechTranscriber
{
    public const string EncoderFile = "encoder.int8.onnx";
    public const string DecoderFile = "decoder.int8.onnx";
    public const string JoinerFile = "joiner.int8.onnx";
    public const string TokensFile = "tokens.txt";

    /// Longest piece decoded at once, and how far back from it the cut may move to find a pause.
    public const double MaxChunkSeconds = 60;
    public const double CutSearchSeconds = 15;

    private const int SampleRate = 16_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineRecognizer? _recognizer;

    private ParakeetTranscriber(string modelId, OfflineRecognizer recognizer)
    {
        ModelId = modelId;
        _recognizer = recognizer;
    }

    public string ModelId { get; }

    /// Loads the model from a folder with encoder, decoder, joiner and tokens. Takes a few
    /// seconds; call it off the UI thread (LocalTranscriptionService does).
    public static ParakeetTranscriber Load(string directory, string modelId = ModelCatalog.ParakeetId, int? threads = null)
    {
        var encoder = Path.Combine(directory, EncoderFile);
        var decoder = Path.Combine(directory, DecoderFile);
        var joiner = Path.Combine(directory, JoinerFile);
        var tokens = Path.Combine(directory, TokensFile);
        // sherpa-onnx returns no error for a missing file, it fails later inside native code.
        foreach (var file in new[] { encoder, decoder, joiner, tokens })
        {
            if (!File.Exists(file)) throw new TranscriptionException(TranscriptionError.ModelIncomplete, TranscriptionException.IncompleteMessage(modelId));
        }

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        // The feature dimension (128 mel bins for v3) and NeMo normalization come from the model's metadata.
        config.ModelConfig.Transducer.Encoder = encoder;
        config.ModelConfig.Transducer.Decoder = decoder;
        config.ModelConfig.Transducer.Joiner = joiner;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = threads ?? DefaultThreads();
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        var recognizer = new OfflineRecognizer(config);
        return new ParakeetTranscriber(modelId, recognizer);
    }

    /// Half the logical processors (2 to 8): fast, and the PC stays responsive.
    public static int DefaultThreads() => Math.Clamp(Environment.ProcessorCount / 2, Math.Min(2, Environment.ProcessorCount), 8);

    public async Task<TranscriptionResult> TranscribeAsync(float[] samples16kMono, string language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(samples16kMono);
        if (samples16kMono.Length == 0) return TranscriptionResult.Empty;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var recognizer = _recognizer ?? throw new ObjectDisposedException(nameof(ParakeetTranscriber));
            return await Task.Run(() => Transcribe(recognizer, samples16kMono, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TranscriptionResult Transcribe(OfflineRecognizer recognizer, float[] samples, CancellationToken ct)
    {
        var texts = new List<string>();
        var words = new List<TimedWord>();
        foreach (var chunk in AudioChunker.Split(samples, MaxChunkSeconds, CutSearchSeconds))
        {
            ct.ThrowIfCancellationRequested();
            var piece = chunk.Start == 0 && chunk.Length == samples.Length ? samples : samples.AsSpan(chunk.Start, chunk.Length).ToArray();
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(SampleRate, piece);
            recognizer.Decode(stream);
            var result = stream.Result;
            var text = result.Text.Trim();
            if (text.Length > 0) texts.Add(text);
            words.AddRange(Words(result, chunk.StartSeconds, chunk.Length / (double)SampleRate));
        }
        return new TranscriptionResult(string.Join(' ', texts), null, words);
    }

    /// Token times are starts; TDT also reports durations. Without them a token lasts until the next one.
    private static IEnumerable<TimedWord> Words(OfflineRecognizerResult result, double offset, double chunkSeconds)
    {
        var tokens = result.Tokens ?? [];
        var starts = result.Timestamps;
        if (starts is null || starts.Length != tokens.Length) return [];
        var durations = result.Durations;
        var timed = new List<(string, double, double)>(tokens.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            double start = starts[i];
            double end = durations is { } d && d.Length == tokens.Length && d[i] > 0
                ? start + d[i]
                : i + 1 < tokens.Length ? starts[i + 1] : Math.Min(chunkSeconds, start + 0.08);
            timed.Add((tokens[i], offset + start, offset + Math.Max(start, end)));
        }
        return TimedWord.FromTokens(timed);
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
