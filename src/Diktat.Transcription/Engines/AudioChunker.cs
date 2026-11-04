namespace Diktat.Transcription.Engines;

/// Splits long recordings into pieces a model handles in one pass, cutting in the quietest
/// moment near the end of each window, so a word is practically never cut in half.
public static class AudioChunker
{
    public readonly record struct Chunk(int Start, int Length)
    {
        public int End => Start + Length;
        public double StartSeconds => Start / 16_000.0;
    }

    /// <param name="maxSeconds">Longest piece.</param>
    /// <param name="searchSeconds">How far before the window end the cut may move to find a pause.</param>
    public static List<Chunk> Split(ReadOnlySpan<float> samples, double maxSeconds, double searchSeconds, int sampleRate = 16_000)
    {
        var chunks = new List<Chunk>();
        var max = Math.Max(sampleRate, (int)(maxSeconds * sampleRate));
        var search = Math.Clamp((int)(searchSeconds * sampleRate), 0, max / 2);
        var frame = Math.Max(1, sampleRate / 50); // 20 ms
        var start = 0;
        while (samples.Length - start > max)
        {
            var windowEnd = start + max;
            var searchStart = windowEnd - search;
            var cut = windowEnd;
            var quietest = double.MaxValue;
            // Prefer later cuts on ties (longer pieces, fewer cuts).
            for (var position = windowEnd - frame; position >= searchStart; position -= frame)
            {
                double energy = 0;
                foreach (var s in samples.Slice(position, frame)) energy += s * s;
                if (energy < quietest * 0.999)
                {
                    quietest = energy;
                    cut = position + frame / 2;
                }
            }
            chunks.Add(new Chunk(start, cut - start));
            start = cut;
        }
        if (samples.Length - start > 0) chunks.Add(new Chunk(start, samples.Length - start));
        return chunks;
    }
}
