namespace Diktat.Transcription.Meeting;

/// Removes from the microphone what it only heard from the PC's speakers. In an online
/// meeting without headphones the other side comes out of the speakers and into the
/// microphone, so the same words show up in both tracks at about the same time.
/// Port of the macOS EchoFilter.
public static class EchoFilter
{
    /// How far apart, after aligning both tracks, the same word may be and still count as echo.
    public const double Window = 0.6;
    /// The two tracks start a moment apart and the speakers add a little delay.
    public const double MaximumOffset = 2.0;
    /// Scraps of garbled echo with at most this many words are dropped while others talk.
    public const int FragmentWords = 4;
    public const double EchoNearby = 20;

    public static List<TimedWord> RemoveEcho(IReadOnlyList<TimedWord> microphone, IReadOnlyList<TimedWord> system)
    {
        if (microphone.Count == 0 || system.Count == 0) return [.. microphone];
        var timesByKey = new Dictionary<string, List<double>>();
        foreach (var word in system)
        {
            var key = Key(word.Text);
            if (key.Length == 0) continue;
            if (!timesByKey.TryGetValue(key, out var list)) timesByKey[key] = list = [];
            list.Add(word.Start);
        }
        foreach (var list in timesByKey.Values) list.Sort();

        var keys = microphone.Select(w => Key(w.Text)).ToArray();
        var offset = BestOffset(microphone, keys, timesByKey);
        var matches = microphone.Select((w, i) =>
            keys[i].Length > 0 && HasTime(w.Start - offset, TimesFor(timesByKey, keys[i]), Window)).ToArray();

        var echo = new bool[microphone.Count];
        // A single matching word is often coincidence ("ja", "und"); echo comes in runs.
        // Single letters carry no key and do not break a run.
        var significant = Enumerable.Range(0, microphone.Count).Where(i => keys[i].Length > 0).ToArray();
        for (var position = 0; position < significant.Length; position++)
        {
            var index = significant[position];
            if (!matches[index]) continue;
            var previous = position > 0 && matches[significant[position - 1]];
            var next = position + 1 < significant.Length && matches[significant[position + 1]];
            echo[index] = previous || next;
        }
        // Words the microphone garbled inside a run of echo belong to it as well.
        var marked = (bool[])echo.Clone();
        for (var index = 0; index < microphone.Count; index++)
        {
            if (marked[index]) continue;
            var before = false;
            for (var j = Math.Max(0, index - 2); j < index; j++) before |= marked[j];
            var after = false;
            for (var j = index + 1; j < Math.Min(microphone.Count, index + 3); j++) after |= marked[j];
            echo[index] = before && after;
        }
        RemoveFragments(echo, microphone, keys, matches, system, offset);
        return microphone.Where((_, i) => !echo[i]).ToList();
    }

    /// What is left of a garbled echo: short scraps while the other side is talking. Dropped
    /// when they have at most <see cref="FragmentWords"/> words or when much of them also is in
    /// the system sound, and only where the microphone demonstrably picked up the speakers.
    /// With headphones there is no echo, so short replies like "Mach ich" stay.
    private static void RemoveFragments(bool[] echo, IReadOnlyList<TimedWord> microphone, string[] keys, bool[] matches, IReadOnlyList<TimedWord> system, double offset)
    {
        var systemStarts = system.Select(w => w.Start).OrderBy(t => t).ToList();
        var echoStarts = Enumerable.Range(0, microphone.Count).Where(i => echo[i]).Select(i => microphone[i].Start).OrderBy(t => t).ToList();
        var segments = new List<List<int>>();
        for (var index = 0; index < microphone.Count; index++)
        {
            if (echo[index]) continue;
            if (segments.Count > 0 && microphone[index].Start - microphone[segments[^1][^1]].End <= 1.0)
            {
                segments[^1].Add(index);
            }
            else
            {
                segments.Add([index]);
            }
        }
        foreach (var segment in segments)
        {
            var first = segment[0];
            var last = segment[^1];
            var start = microphone[first].Start - offset;
            var end = microphone[last].End - offset;
            var othersTalking = HasTime((start + end) / 2, systemStarts, (end - start) / 2 + 0.5);
            if (!othersTalking) continue;
            var significant = segment.Where(i => keys[i].Length > 0).ToList();
            var matched = significant.Count(i => matches[i]);
            var middle = (microphone[first].Start + microphone[last].End) / 2;
            var span = (microphone[last].End - microphone[first].Start) / 2;
            var speakersAudible = matched >= 2 || HasTime(middle, echoStarts, span + EchoNearby);
            if (!speakersAudible) continue;
            var mostlyEcho = significant.Count > 0 && (double)matched / significant.Count >= 0.4;
            if (segment.Count <= FragmentWords || mostlyEcho)
            {
                foreach (var index in segment) echo[index] = true;
            }
        }
    }

    /// The shift between the tracks at which most microphone words find themselves in the
    /// system sound; 0 when there is hardly any echo, e.g. with headphones.
    private static double BestOffset(IReadOnlyList<TimedWord> microphone, string[] keys, Dictionary<string, List<double>> timesByKey)
    {
        var bestOffset = 0.0;
        var bestCount = 0;
        var steps = (int)(MaximumOffset * 10);
        for (var step = -steps; step <= steps; step++)
        {
            var offset = step / 10.0;
            var count = 0;
            for (var i = 0; i < microphone.Count; i++)
            {
                if (HasTime(microphone[i].Start - offset, TimesFor(timesByKey, keys[i]), 0.3)) count++;
            }
            if (count > bestCount)
            {
                bestOffset = offset;
                bestCount = count;
            }
        }
        return bestCount >= 3 ? bestOffset : 0;
    }

    private static List<double> TimesFor(Dictionary<string, List<double>> timesByKey, string key) =>
        key.Length > 0 && timesByKey.TryGetValue(key, out var list) ? list : [];

    /// Whether the sorted <paramref name="times"/> hold a value within <paramref name="tolerance"/> of <paramref name="time"/>.
    private static bool HasTime(double time, List<double> times, double tolerance)
    {
        int low = 0, high = times.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (times[middle] < time - tolerance) low = middle + 1; else high = middle;
        }
        return low < times.Count && times[low] <= time + tolerance;
    }

    /// "Kanal," and "kanal" are the same word; single letters say too little.
    private static string Key(string word)
    {
        var letters = new string(word.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return letters.Length > 1 ? letters : "";
    }
}
