using Diktat.Core.Settings;

namespace Diktat.Core.Text;

/// Website addresses for "Modus je App": what people type, what browsers report.
public static class WebsiteHost
{
    /// "https://www.Mail.google.com/mail/u/0" or "mail.google.com" -> "mail.google.com". Null for
    /// anything that is not a host name.
    public static string? Normalized(string? input)
    {
        var trimmed = (input ?? "").Trim().ToLowerInvariant();
        if (trimmed.Length == 0 || trimmed.Contains(' ')) return null;
        var withScheme = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host;
        if (string.IsNullOrEmpty(host) || !host.Contains('.') || host.StartsWith('.') || host.EndsWith('.')) return null;
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return host;
    }

    /// A rule for "google.com" also covers "mail.google.com", not "notgoogle.com".
    public static bool Matches(string host, string rule)
    {
        var lowered = host.ToLowerInvariant();
        return lowered == rule || lowered.EndsWith("." + rule, StringComparison.Ordinal);
    }
}

/// Program identity on Windows: the lower-case file name of the executable ("outlook.exe").
public static class ProcessNames
{
    /// "C:\Program Files\Outlook\OUTLOOK.EXE", "Outlook" or "outlook.exe" -> "outlook.exe".
    public static string Normalize(string? processName)
    {
        var name = (processName ?? "").Trim();
        var slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) name = name[(slash + 1)..];
        name = name.ToLowerInvariant();
        if (name.Length > 0 && !name.EndsWith(".exe", StringComparison.Ordinal)) name += ".exe";
        return name;
    }

    /// Browsers whose address the Windows app reads, for website rules and website contexts.
    public static readonly IReadOnlySet<string> Browsers = new HashSet<string>(StringComparer.Ordinal)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "arc.exe",
        "chromium.exe", "librewolf.exe", "waterfox.exe", "zen.exe",
    };

    public static bool IsBrowser(string? processName) => Browsers.Contains(Normalize(processName));
}

/// "Modus je App": which mode a shortcut starts in the program in front.
public static class ModeRules
{
    /// The mode a shortcut starts. Only the Diktat shortcut (<see cref="WorkflowType.TapTalk"/>)
    /// follows the rules; the other shortcuts stay explicit. A website rule wins over the rule for
    /// its browser, and the most specific host wins among website rules. A rule whose mode cannot
    /// run (<paramref name="isAvailable"/> false, e.g. an AI mode without a provider) is skipped,
    /// so dictation falls back to word-for-word. <paramref name="host"/> is the website when the
    /// program is a browser, else null.
    public static WorkflowType Resolve(
        WorkflowType requested,
        string? processName,
        string? host,
        AppSettings settings,
        Func<WorkflowType, bool>? isAvailable = null)
    {
        if (requested != WorkflowType.TapTalk) return requested;
        isAvailable ??= _ => true;

        if (!string.IsNullOrWhiteSpace(host) && settings.WebsiteModeRules.Count > 0)
        {
            var rule = settings.WebsiteModeRules
                .Where(r => !string.IsNullOrWhiteSpace(r.Host) && WebsiteHost.Matches(host, r.Host.ToLowerInvariant()))
                .OrderByDescending(r => r.Host.Length)
                .FirstOrDefault();
            if (rule is not null && isAvailable(rule.Mode)) return rule.Mode;
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            var name = ProcessNames.Normalize(processName);
            var rule = settings.AppModeRules.FirstOrDefault(r => ProcessNames.Normalize(r.ProcessName) == name);
            if (rule is not null && isAvailable(rule.Mode)) return rule.Mode;
        }
        return requested;
    }

    /// Website presets the settings offer when adding a website rule.
    public static IReadOnlyList<(string Name, string Host)> WebsitePresets =>
    [
        ("Gmail", "mail.google.com"),
        (L.T("Outlook im Web"), "outlook.office.com"),
        ("Google Docs", "docs.google.com"),
        ("Notion", "notion.so"),
        ("Slack", "app.slack.com"),
        ("Microsoft Teams", "teams.microsoft.com"),
        ("WhatsApp Web", "web.whatsapp.com"),
        ("LinkedIn", "linkedin.com"),
    ];

    /// The modes a rule may choose.
    public static readonly IReadOnlyList<WorkflowType> SelectableModes =
        [WorkflowType.TapTalk, WorkflowType.TextImprover, WorkflowType.DampfAblassen, WorkflowType.EmojiText];
}
