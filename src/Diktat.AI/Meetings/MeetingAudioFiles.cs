namespace Diktat.AI.Meetings;

/// The recordings of a meeting on disk until it is evaluated: "&lt;id&gt;-mic.wav" for the
/// microphone and "&lt;id&gt;-system.wav" for what the PC played.
public static class MeetingAudioFiles
{
    private const string MicrophoneSuffix = "-mic.wav";
    private const string SystemAudioSuffix = "-system.wav";

    public static string MicrophoneFile(Guid id) => id + MicrophoneSuffix;
    public static string SystemAudioFile(Guid id) => id + SystemAudioSuffix;

    /// The meeting a recording belongs to; null for a file that is no recording.
    public static Guid? MeetingOf(string name)
    {
        foreach (var suffix in new[] { MicrophoneSuffix, SystemAudioSuffix })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(name[..^suffix.Length], "D", out var id)) return id;
        }
        return null;
    }

    /// The names in <paramref name="names"/> that are recordings of none of the meetings
    /// <paramref name="keeping"/>. Files that are no recording are never named.
    public static List<string> Orphaned(IEnumerable<string> names, IReadOnlySet<Guid> keeping) =>
        names.Where(name => MeetingOf(name) is { } id && !keeping.Contains(id)).ToList();
}
