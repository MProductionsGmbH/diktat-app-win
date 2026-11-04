namespace Diktat.Core.Audio;

/// A little silence before and after a dictation. Speech models tend to drop a word that starts
/// right at the first sample, and Bluetooth microphones begin with a moment of digital silence
/// that cuts into the first word; padding keeps the first and last word.
///
/// The macOS app pads a copy of the recorded file; here it works on the samples, which the
/// Windows recorder keeps in memory (mono float PCM).
public static class AudioPadding
{
    public const double Seconds = 0.3;
    /// Longer recordings, e.g. audio files and meetings, start with room anyway.
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(600);

    /// A padded copy, or null when the sample rate is invalid or the recording is long enough not
    /// to need it.
    public static float[]? PaddedCopy(ReadOnlySpan<float> samples, double sampleRate, double seconds = Seconds)
    {
        if (sampleRate <= 0 || samples.Length / sampleRate > MaximumDuration.TotalSeconds) return null;
        var silence = (int)(seconds * sampleRate);
        var result = new float[samples.Length + 2 * silence];
        samples.CopyTo(result.AsSpan(silence));
        return result;
    }
}

/// Scales the input level to the microphone at hand. A webcam microphone a metre away reaches 0.3
/// when someone speaks, a headset 0.8; the bars of the recording pill and the tray icon should
/// use their full height for both. The loudest recent level counts as full scale, falling back
/// slowly (about two seconds to half), and never below a floor, so room noise does not fill it.
public sealed class AdaptiveLevel
{
    /// The level that counts as full scale at least: a quiet room stays quiet on the bars.
    public const float MinimumPeak = 0.25f;
    /// Below this the input counts as silence.
    public const float Floor = 0.05f;
    /// Per call; the recorder calls it every 50 ms, so this halves in about two seconds.
    private const float Decay = 0.985f;

    public float Peak { get; private set; } = MinimumPeak;

    public float Normalize(float raw)
    {
        Peak = Math.Max(raw, Math.Max(Peak * Decay, MinimumPeak));
        return Math.Min(1, Math.Max(0, (raw - Floor) / (Peak - Floor)));
    }

    public void Reset() => Peak = MinimumPeak;
}
