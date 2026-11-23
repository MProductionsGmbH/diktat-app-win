using System.Globalization;

namespace Diktat.Core.Text;

/// Swiss German writes "ss" where Germany and Austria write the sharp s: "Strasse", "gross",
/// "schliessen". With the setting on, everything Diktat writes follows that: dictations, the
/// texts of the AI modes, meeting transcripts, summaries and live notes (SwissSpelling.swift).
public static class SwissSpelling
{
    private static volatile bool _isEnabled;

    /// The setting "Schweizer Schreibweise" for code without the settings at hand (meeting
    /// summaries, live notes); AppServices keeps it current.
    public static bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    /// Where "ss" is the rule: Switzerland and Liechtenstein. The setting starts on there.
    public static bool IsUsual(string? region) =>
        string.Equals(region, "CH", StringComparison.OrdinalIgnoreCase) || string.Equals(region, "LI", StringComparison.OrdinalIgnoreCase);

    /// Whether the Windows region (the format settings) is one where "ss" is the rule.
    public static bool IsUsualHere
    {
        get
        {
            try { return IsUsual(RegionInfo.CurrentRegion.TwoLetterISORegionName); }
            catch (ArgumentException) { return false; }
        }
    }

    public static string Apply(string text)
    {
        if (string.IsNullOrEmpty(text) || (!text.Contains('ß') && !text.Contains('ẞ'))) return text;
        return text.Replace("ß", "ss").Replace("ẞ", "SS");
    }

    /// <paramref name="text"/> in the Swiss spelling when <paramref name="enabled"/>, otherwise as it is.
    public static string IfEnabled(string text, bool enabled) => enabled ? Apply(text) : text;

    /// <paramref name="text"/> in the Swiss spelling when the setting is on, otherwise as it is.
    public static string IfEnabled(string text) => IfEnabled(text, IsEnabled);
}
