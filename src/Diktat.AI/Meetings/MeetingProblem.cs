namespace Diktat.AI.Meetings;

/// Something is wrong with a running recording, and whoever runs the meeting has to know now:
/// what is not recorded cannot be brought back afterwards (MeetingProblem.swift). Shown large on
/// the meeting page, in the recording pill, in the tray and as a notification.
/// The order is the urgency: the smallest value is the most urgent.
public enum MeetingProblem
{
    /// No microphone opens; the recording keeps trying.
    MicrophoneLost,
    /// The microphone is open but delivers digital silence: muted, or a laptop with the lid closed.
    MicrophoneSilent,
    /// The output device is gone, so the PC's sound cannot be recorded; the recording keeps trying.
    SystemAudioLost,
}

public static class MeetingProblemInfo
{
    /// How long a microphone may deliver nothing but digital silence before it counts as a problem.
    public static readonly TimeSpan SilenceBeforeWarning = TimeSpan.FromSeconds(5);

    /// Digital silence reads about -120 dB; the quietest real room is far above -110.
    public const float SilenceLevelDb = -110f;

    public static string Title(this MeetingProblem problem) => problem switch
    {
        MeetingProblem.MicrophoneLost => L.T("Das Mikrofon ist nicht erreichbar"),
        MeetingProblem.MicrophoneSilent => L.T("Das Mikrofon liefert keinen Ton"),
        _ => L.T("Das System-Audio ist nicht erreichbar"),
    };

    public static string Detail(this MeetingProblem problem) => problem switch
    {
        MeetingProblem.MicrophoneLost => L.T("Diktat findet gerade kein Mikrofon und versucht es weiter. Bis dahin fehlt im Transkript, was gesagt wird. Ist das Mikrofon angeschlossen?"),
        MeetingProblem.MicrophoneSilent => L.T("Vom Mikrofon kommt nur Stille. Bis es wieder Ton liefert, fehlt im Transkript, was gesagt wird. Ist es stumm geschaltet oder der Laptop zugeklappt?"),
        _ => L.T("Das Ausgabegerät ist weg. Diktat versucht es weiter. Bis dahin fehlen im Transkript die anderen."),
    };

    /// For the recording pill, which has room for two words.
    public static string ShortLabel(this MeetingProblem problem) => problem switch
    {
        MeetingProblem.MicrophoneLost or MeetingProblem.MicrophoneSilent => L.T("Mikrofon prüfen"),
        _ => L.T("Ton prüfen"),
    };
}

/// The problems of a running recording at once; <see cref="Current"/> is the most urgent.
public sealed class MeetingProblems
{
    private readonly HashSet<MeetingProblem> _active = [];

    public MeetingProblem? Current => _active.Count == 0 ? null : _active.Min();

    /// Turns one problem on or off. True when <see cref="Current"/> changed.
    public bool Set(MeetingProblem problem, bool isActive)
    {
        var before = Current;
        if (isActive) _active.Add(problem);
        else _active.Remove(problem);
        return before != Current;
    }

    public void Clear() => _active.Clear();
}

/// Tells when a microphone has delivered nothing but digital silence for too long.
public sealed class MicrophoneSilenceTracker
{
    private DateTimeOffset? _silentSince;

    /// Takes the level of the newest audio and says whether the silence now is a problem.
    public bool IsSilentTooLong(float powerDb, DateTimeOffset now)
    {
        if (powerDb >= MeetingProblemInfo.SilenceLevelDb)
        {
            _silentSince = null;
            return false;
        }
        _silentSince ??= now;
        return now - _silentSince.Value >= MeetingProblemInfo.SilenceBeforeWarning;
    }

    public void Reset() => _silentSince = null;
}
