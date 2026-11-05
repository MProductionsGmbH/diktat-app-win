using Diktat.Core.Settings;
using Diktat.Core.Text;
using Diktat.Transcription;
using Diktat.Transcription.Audio;
using Diktat.Windows.Platform;

namespace Diktat.Windows.Runtime;

/// Transcribes an audio file on this PC: Voice Memos (m4a), WhatsApp voice messages (Opus in
/// Ogg), MP3, WAV and whatever Media Foundation decodes (AudioFileController.swift). The text
/// goes into the history; what happens next (copy, rewrite) is the window's business.
public sealed class AudioFileTranscriber
{
    private readonly AppServices _services;
    private readonly AudioFileDecoder _decoder = new(new MediaFoundationDecoder());

    public AudioFileTranscriber(AppServices services)
    {
        _services = services;
    }

    /// The extensions the decoders read, for the file dialog.
    public static IReadOnlyList<string> Extensions { get; } =
        AudioFileDecoder.BuiltInExtensions.Concat(MediaFoundationDecoder.Extensions).Distinct().ToList();

    public async Task<string> TranscribeAsync(string path, bool paragraphs, IProgress<string>? status, CancellationToken cancellationToken)
    {
        if (!_services.Transcription.HasModel) throw TranscriptionException.NoModel();
        var settings = _services.Settings;
        var language = settings.Transcription.Language;

        status?.Report(L.T("Wird umgewandelt ..."));
        var samples = await _decoder.DecodeToMono16kAsync(path, cancellationToken);
        if (samples.Length == 0) throw AudioDecodeException.Empty();
        Log.Info($"Audio file decoded: {samples.Length / 16_000.0:F0} s");

        if (!_services.Transcription.IsReady)
        {
            status?.Report(DictationRules.LoadingModelStatus);
            await _services.Transcription.WarmUpAsync(cancellationToken);
        }
        status?.Report(DictationRules.TranscribingStatus);
        var result = await _services.Transcription.TranscribeAllowingEmptyAsync(samples, language, cancellationToken);
        samples = [];

        var processed = TranscriptPostProcessor.FromSettings(settings, _services.SpellChecker)
            .Process(TranscriptionQualityService.CleanedTranscript(result.Text)).Trim();
        if (paragraphs) processed = TranscriptParagraphService.Format(processed, language);
        if (processed.Length == 0)
        {
            throw new TranscriptionException(TranscriptionError.NoText, L.T("In der Datei wurde keine Sprache erkannt."));
        }
        _services.History.Add(processed, WorkflowType.TapTalk);
        return processed;
    }
}
