namespace Diktat.Transcription.Tests;

internal static class TestAudio
{
    public static float[] Sine(double frequency, int sampleRate, double seconds, float amplitude = 0.5f)
    {
        var samples = new float[(int)(sampleRate * seconds)];
        for (var i = 0; i < samples.Length; i++) samples[i] = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / sampleRate);
        return samples;
    }

    public static float[] Interleave(float[] left, float[] right)
    {
        var result = new float[left.Length * 2];
        for (var i = 0; i < left.Length; i++)
        {
            result[2 * i] = left[i];
            result[2 * i + 1] = right[i];
        }
        return result;
    }

    /// Frequency estimate from upward zero crossings, ignoring the edges.
    public static double Frequency(ReadOnlySpan<float> samples, int sampleRate)
    {
        var skip = samples.Length / 10;
        var crossings = 0;
        int first = -1, last = -1;
        for (var i = skip + 1; i < samples.Length - skip; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0)
            {
                if (first < 0) first = i;
                last = i;
                crossings++;
            }
        }
        return crossings < 2 ? 0 : (crossings - 1) * (double)sampleRate / (last - first);
    }

    public static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        return samples.Length == 0 ? 0 : Math.Sqrt(sum / samples.Length);
    }

    public static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "diktat-transcription-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
