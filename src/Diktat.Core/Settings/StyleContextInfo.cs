using Diktat.Core.Text;

namespace Diktat.Core.Settings;

/// Where the dictated text goes, so it can be written in the tone that suits it: a message to a
/// friend reads differently from a mail to a client.
public static class StyleContextInfo
{
    public static readonly StyleContext[] AllCases = [StyleContext.Personal, StyleContext.Work, StyleContext.Email, StyleContext.Other];

    public static string DisplayName(this StyleContext context) => context switch
    {
        StyleContext.Personal => "Persönlich",
        StyleContext.Work => "Arbeit",
        StyleContext.Email => "E-Mail",
        _ => "Andere",
    };

    /// Shown under the tabs, so it is clear where the style applies.
    public static string AppsNote(this StyleContext context) => context switch
    {
        StyleContext.Personal => "Gilt in WhatsApp, Telegram, Signal und Discord.",
        StyleContext.Work => "Gilt in Slack, Teams und Zoom, auch im Browser.",
        StyleContext.Email => "Gilt in Outlook, Thunderbird, Spark und anderen Mail-Programmen, auch Gmail im Browser.",
        _ => "Gilt in allen anderen Programmen.",
    };

    public static StyleTone DefaultTone(this StyleContext context) => StyleSettings.DefaultTone(context);

    /// Programs by their lower-case executable name (the macOS app maps bundle IDs).
    private static readonly Dictionary<string, StyleContext> ProcessContexts = new(StringComparer.Ordinal)
    {
        ["whatsapp.exe"] = StyleContext.Personal,
        ["whatsapp.root.exe"] = StyleContext.Personal,
        ["telegram.exe"] = StyleContext.Personal,
        ["signal.exe"] = StyleContext.Personal,
        ["discord.exe"] = StyleContext.Personal,
        ["slack.exe"] = StyleContext.Work,
        ["ms-teams.exe"] = StyleContext.Work,
        ["teams.exe"] = StyleContext.Work,
        ["zoom.exe"] = StyleContext.Work,
        ["outlook.exe"] = StyleContext.Email,
        ["olk.exe"] = StyleContext.Email, // the new Outlook
        ["thunderbird.exe"] = StyleContext.Email,
        ["mailbird.exe"] = StyleContext.Email,
        ["mailclient.exe"] = StyleContext.Email, // eM Client
        ["spark.exe"] = StyleContext.Email,
        ["hxoutlook.exe"] = StyleContext.Email,
    };

    /// Websites in a browser, matched like the rules in "Modus je App": a host also covers its subdomains.
    private static readonly (string Host, StyleContext Context)[] Hosts =
    [
        ("web.whatsapp.com", StyleContext.Personal), ("web.telegram.org", StyleContext.Personal),
        ("discord.com", StyleContext.Personal), ("messenger.com", StyleContext.Personal),
        ("app.slack.com", StyleContext.Work), ("slack.com", StyleContext.Work),
        ("teams.microsoft.com", StyleContext.Work), ("linkedin.com", StyleContext.Work),
        ("mail.google.com", StyleContext.Email), ("outlook.office.com", StyleContext.Email),
        ("outlook.live.com", StyleContext.Email), ("outlook.office365.com", StyleContext.Email),
        ("mail.proton.me", StyleContext.Email), ("app.hey.com", StyleContext.Email),
    ];

    /// The context of the program, or of the website when the program is a browser and the host is known.
    public static StyleContext Detect(string? processName, string? host)
    {
        if (!string.IsNullOrWhiteSpace(host))
        {
            var normalized = host.Trim().ToLowerInvariant();
            foreach (var (ruleHost, context) in Hosts)
            {
                if (normalized == ruleHost || normalized.EndsWith("." + ruleHost, StringComparison.Ordinal)) return context;
            }
        }
        if (!string.IsNullOrWhiteSpace(processName) && ProcessContexts.TryGetValue(ProcessNames.Normalize(processName), out var appContext))
        {
            return appContext;
        }
        return StyleContext.Other;
    }
}

/// How the text reads: the sentence mark it ends with and, when the AI written-style level
/// rewrites it, the wording.
public static class StyleToneInfo
{
    public static readonly StyleTone[] AllCases = [StyleTone.Formal, StyleTone.Neutral, StyleTone.Casual];

    public static string DisplayName(this StyleTone tone) => tone switch
    {
        StyleTone.Formal => "Formell",
        StyleTone.Neutral => "Normal",
        _ => "Locker",
    };

    public static string Detail(this StyleTone tone) => tone switch
    {
        StyleTone.Formal => "Mit KI-Schreibstil: sachlich und höflich formuliert",
        StyleTone.Neutral => "Mit KI-Schreibstil: klar und neutral formuliert",
        _ => "Ohne Punkt am Ende, mit KI-Schreibstil ungezwungen formuliert",
    };

    /// The same dictated sentence for every tone, so the settings show what each tone changes.
    public const string ExampleSpoken = "hey alex war echt schön heute mit dir zu reden ich melde mich nächste woche wegen dem termin";

    /// The sentence after the cleanup levels Leicht to Stark: punctuation set, and only the
    /// period at the end follows the tone.
    public static string ExampleCleaned(this StyleTone tone) =>
        tone.Apply("Hey Alex, war echt schön, heute mit dir zu reden. Ich melde mich nächste Woche wegen dem Termin.");

    /// What the model of KI-Schreibstil made of it in this tone (a model words it a little
    /// differently each time).
    public static string ExampleRewritten(this StyleTone tone) => tone switch
    {
        StyleTone.Formal => tone.Apply("Hallo Alex, es war wirklich schön, heute mit dir zu sprechen. Ich melde mich nächste Woche bezüglich des Termins."),
        StyleTone.Neutral => tone.Apply("Hey Alex, es war wirklich schön, heute mit dir zu reden. Ich melde mich nächste Woche wegen des Termins."),
        _ => tone.Apply("Hey Alex, war echt schön, heute mit dir zu reden. Ich melde mich nächste Woche wegen des Termins."),
    };

    /// The tone for the model of the AI written-style level.
    public static TextTone RewriteTone(this StyleTone tone) => tone switch
    {
        StyleTone.Formal => TextTone.Formal,
        StyleTone.Neutral => TextTone.Neutral,
        _ => TextTone.Casual,
    };

    /// Rule-based part: the casual tone leaves the period off the end, as people do in a chat. A
    /// question or exclamation mark and an ellipsis stay.
    public static string Apply(this StyleTone tone, string text)
    {
        if (tone != StyleTone.Casual) return text;
        var trimmed = text.Trim();
        if (!trimmed.EndsWith('.') || trimmed.EndsWith("..", StringComparison.Ordinal)) return text;
        return trimmed[..^1];
    }
}
