using System.Runtime.InteropServices;

namespace Diktat.AI;

/// "Feedback senden": a prepared mail to the address below with the facts that help to understand
/// a report. People see the whole mail before it goes out; the app itself sends nothing and adds
/// no logs.
public static class FeedbackMail
{
    /// Empty until the project has a feedback address; the button in the settings stays hidden.
    public const string Address = "";

    public static bool IsAvailable => Address.Length > 0;

    /// The mailto: link, or null while there is no address.
    /// <param name="version">The app version, e.g. "0.1.0".</param>
    /// <param name="build">The build number, e.g. the file version's last part.</param>
    /// <param name="details">Lines about this app's setup, e.g. speech model and shortcut mode.</param>
    /// <param name="deviceModel">The PC model and processor if known, e.g. "Surface Laptop 7, Snapdragon X Elite".</param>
    public static string? Url(string version, string build, IEnumerable<string>? details = null, string? deviceModel = null) =>
        IsAvailable ? Url(Address, version, build, details, deviceModel) : null;

    internal static string Url(string address, string version, string build, IEnumerable<string>? details, string? deviceModel)
    {
        var facts = new List<string>
        {
            L.F("App: Diktat {0} (Build {1}) für {2}", version, build, "Windows"),
            $"System: Windows {Environment.OSVersion.Version} ({RuntimeInformation.OSArchitecture})",
            L.F("Gerät: {0}", string.IsNullOrWhiteSpace(deviceModel) ? RuntimeInformation.ProcessArchitecture.ToString() : deviceModel),
        };
        facts.AddRange(details ?? []);
        var subject = L.F("Diktat Feedback ({0} {1}, Build {2})", "Windows", version, build);
        var body = L.T("\n\n\n---\nBitte oberhalb der Linie schreiben. Diese Angaben helfen uns beim Nachvollziehen, du kannst sie auch löschen:\n")
            + string.Join("\n", facts);
        return $"mailto:{address}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";
    }
}
