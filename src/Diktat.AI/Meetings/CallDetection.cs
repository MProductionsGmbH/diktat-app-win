namespace Diktat.AI.Meetings;

/// Notices that a call app has the microphone open, so Diktat can suggest recording the
/// meeting with the PC's sound (CallDetection.swift). It reads only which apps use audio right
/// now (the list Windows shows under Privacy, Microphone), never what they play or record.
public static class CallDetection
{
    /// An app that uses audio. <see cref="App"/> is the program file name ("zoom.exe") or, for a
    /// Store app, its package family name ("MSTeams_8wekyb3d8bbwe").
    public sealed record Candidate(string App, bool IsRunningInput, bool IsRunningOutput);

    /// Apps made for calls: an open microphone is enough. Matched by the start of the name,
    /// ignoring case.
    public static readonly IReadOnlyList<(string Prefix, string Name)> CallApps =
    [
        ("zoom.exe", "Zoom"),
        ("ms-teams.exe", "Teams"),
        ("teams.exe", "Teams"),
        ("msteams_", "Teams"),
        ("microsoftteams_", "Teams"),
        ("webex.exe", "Webex"),
        ("ciscocollabhost.exe", "Webex"),
        ("atmgr.exe", "Webex"),
        ("cisco.webex", "Webex"),
        ("skype.exe", "Skype"),
        ("microsoft.skypeapp_", "Skype"),
        ("discord.exe", "Discord"),
        ("slack.exe", "Slack"),
        ("91750d7e.slack_", "Slack"),
        ("ringcentral", "RingCentral"),
        ("g2mcomm.exe", "GoTo"),
        ("goto.exe", "GoTo"),
        ("gotomeeting", "GoTo"),
        ("bluejeans", "BlueJeans"),
    ];

    /// Browsers run Meet, Teams and others, but also play videos and take dictation, so only
    /// sound in and out together counts as a call.
    public static readonly IReadOnlyList<string> Browsers =
    [
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "arc.exe",
    ];

    /// The names of the apps that look like a call, without repeats and in a steady order.
    public static IReadOnlyList<string> Calls(IEnumerable<Candidate> candidates)
    {
        var names = new List<string>();
        void Add(string name)
        {
            if (!names.Contains(name)) names.Add(name);
        }
        foreach (var candidate in candidates.Where(c => c.IsRunningInput))
        {
            var app = candidate.App.ToLowerInvariant();
            var call = CallApps.FirstOrDefault(a => app.StartsWith(a.Prefix, StringComparison.Ordinal));
            if (call.Name is not null) Add(call.Name);
            else if (candidate.IsRunningOutput && Browsers.Any(b => app.StartsWith(b, StringComparison.Ordinal))) Add("Browser");
        }
        return names;
    }
}

/// Counts the checks of a running call, so a short blip of the microphone is no call and the
/// same app is not suggested again soon (the logic of CallDetector.swift, without the timer).
public sealed class CallStreaks
{
    /// How often the apps are checked.
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    /// A call must last this many checks.
    public const int ChecksBeforeReport = 2;
    /// An app is suggested at most once in this time.
    public static readonly TimeSpan QuietTime = TimeSpan.FromMinutes(15);

    private Dictionary<string, int> _streak = new();
    private readonly Dictionary<string, DateTimeOffset> _lastReport = new();

    /// Steps the counters with the apps in a call right now; returns the apps to report now.
    public IReadOnlyList<string> Evaluate(IReadOnlyList<string> calls, DateTimeOffset now)
    {
        _streak = _streak.Where(s => calls.Contains(s.Key)).ToDictionary(s => s.Key, s => s.Value);
        var report = new List<string>();
        foreach (var name in calls)
        {
            int count = (_streak.TryGetValue(name, out var c) ? c : 0) + 1;
            _streak[name] = count;
            if (count != ChecksBeforeReport) continue;
            if (_lastReport.TryGetValue(name, out var last) && now - last < QuietTime) continue;
            _lastReport[name] = now;
            report.Add(name);
        }
        return report;
    }

    /// Polling stopped (a meeting records): a call has to be seen anew. The quiet time stays.
    public void Reset() => _streak = new();
}
