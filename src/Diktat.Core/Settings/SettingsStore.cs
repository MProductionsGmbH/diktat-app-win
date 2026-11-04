using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diktat.Core.Settings;

/// Loads and saves <see cref="SettingsContainer"/> as one JSON file. Writes are atomic (a
/// temporary file moved over the old one). A file that is no JSON at all is kept as ".bak" and
/// the defaults load; a single bad value (say an unknown enum) only drops that value.
public sealed class SettingsStore
{
    private readonly object _lock = new();

    public SettingsStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public string BackupPath => Path + ".bak";

    public SettingsContainer Load()
    {
        lock (_lock)
        {
            string json;
            try
            {
                if (!File.Exists(Path)) return new SettingsContainer();
                json = File.ReadAllText(Path);
            }
            catch (IOException)
            {
                return new SettingsContainer();
            }
            catch (UnauthorizedAccessException)
            {
                return new SettingsContainer();
            }

            if (TryParseObject(json) is not JsonObject root)
            {
                KeepBackup();
                return new SettingsContainer();
            }
            return FromJson(root);
        }
    }

    public void Save(SettingsContainer settings)
    {
        lock (_lock)
        {
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, SettingsJson.Options));
            File.Move(temporary, Path, overwrite: true);
        }
    }

    /// Reads a settings export of the macOS app ("Einstellungen exportieren"), merges it into the
    /// current settings, saves and returns the result. Throws <see cref="FormatException"/> when
    /// the text is no settings export.
    public SettingsContainer ImportMacExport(string json)
    {
        lock (_lock)
        {
            var merged = MergeMacExport(Load(), json);
            Save(merged);
            return merged;
        }
    }

    /// Keys of the export that stay as they are on this PC: whether the Windows setup was seen and
    /// the release notes confirmed. Keys only the Mac knows (appModeRules with bundle IDs,
    /// customHotkeys with fn keys, the Whisper model name, microphoneUID) have no Windows
    /// property and are ignored anyway.
    private static readonly string[] LocalOnlyAppKeys = ["hasSeenOnboarding", "lastSeenWhatsNewVersion"];

    /// The macOS export merged into <paramref name="current"/>: values in the export win, lists of
    /// custom terms, replacements and website rules are combined (the export wins for the same
    /// spoken words or host), and everything the export does not contain stays.
    public static SettingsContainer MergeMacExport(SettingsContainer current, string json)
    {
        if (TryParseObject(json) is not JsonObject export || !LooksLikeExport(export))
        {
            throw new FormatException("Die Datei ist keine Diktat-Einstellungsdatei.");
        }

        var exported = FromJson(export);
        var root = JsonSerializer.SerializeToNode(current, SettingsJson.Options)!.AsObject();
        foreach (var (sectionName, sectionNode) in export)
        {
            if (sectionNode is not JsonObject section || SectionType(sectionName) is not Type type) continue;
            if (root[sectionName] is not JsonObject target)
            {
                target = new JsonObject();
                root[sectionName] = target;
            }
            var properties = SettingsJson.Options.GetTypeInfo(type).Properties.Select(p => p.Name).ToList();
            foreach (var (key, value) in section)
            {
                if (value is null || sectionName == "app" && LocalOnlyAppKeys.Contains(key)) continue;
                // Keys Windows has no property for, and values that do not fit (an unknown enum
                // value, say), leave the current value alone.
                var property = properties.FirstOrDefault(name => string.Equals(name, key, StringComparison.OrdinalIgnoreCase));
                if (property is null || !Fits(type, new JsonObject { [key] = value.DeepClone() })) continue;
                target[property] = value.DeepClone();
            }
        }

        var merged = FromJson(root);
        // Mac settings saved before English support hold "de", which never was a choice there.
        if (merged.Transcription.Language == "de") merged.Transcription.Language = "auto";

        merged.TextImprovement.CustomTerms = current.TextImprovement.CustomTerms
            .Concat(exported.TextImprovement.CustomTerms)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        merged.TextImprovement.Replacements = current.TextImprovement.Replacements
            .Where(r => !exported.TextImprovement.Replacements.Any(e => SameSpoken(e, r)))
            .Concat(exported.TextImprovement.Replacements)
            .Where(r => !string.IsNullOrWhiteSpace(r.Spoken))
            .ToList();
        merged.App.WebsiteModeRules = current.App.WebsiteModeRules
            .Where(r => !exported.App.WebsiteModeRules.Any(e => string.Equals(e.Host, r.Host, StringComparison.OrdinalIgnoreCase)))
            .Concat(exported.App.WebsiteModeRules)
            .Where(r => !string.IsNullOrWhiteSpace(r.Host))
            .ToList();
        return merged;
    }

    private static bool SameSpoken(TermReplacement a, TermReplacement b) =>
        string.Equals(a.Spoken.Trim(), b.Spoken.Trim(), StringComparison.OrdinalIgnoreCase);

    private static Type? SectionType(string name) => name switch
    {
        "app" => typeof(AppSettings),
        "transcription" => typeof(TranscriptionSettings),
        "textImprovement" => typeof(TextImprovementSettings),
        "dampfAblassen" => typeof(DampfAblassenSettings),
        "emojiText" => typeof(EmojiTextSettings),
        _ => null,
    };

    private static bool Fits(Type type, JsonObject node)
    {
        try
        {
            node.Deserialize(type, SettingsJson.Options);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static bool LooksLikeExport(JsonObject root) =>
        root.ContainsKey("app") || root.ContainsKey("textImprovement") || root.ContainsKey("transcription");

    private static JsonObject? TryParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// Settings from a JSON object, section by section. A value that does not fit its property is
    /// dropped and the property keeps its default; the rest of the section still loads.
    public static SettingsContainer FromJson(JsonObject root)
    {
        JsonObject? Section(string name) =>
            root.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value as JsonObject;

        return new SettingsContainer
        {
            App = Tolerant<AppSettings>(Section("app")),
            Transcription = Tolerant<TranscriptionSettings>(Section("transcription")),
            TextImprovement = Tolerant<TextImprovementSettings>(Section("textImprovement")),
            DampfAblassen = Tolerant<DampfAblassenSettings>(Section("dampfAblassen")),
            EmojiText = Tolerant<EmojiTextSettings>(Section("emojiText")),
        };
    }

    private static T Tolerant<T>(JsonObject? section) where T : new()
    {
        if (section is null) return new T();
        if (TryDeserialize<T>(section, out var whole)) return whole;

        var clean = new JsonObject();
        foreach (var (key, value) in section)
        {
            var single = new JsonObject { [key] = value?.DeepClone() };
            if (TryDeserialize<T>(single, out _)) clean[key] = value?.DeepClone();
        }
        return TryDeserialize<T>(clean, out var result) ? result : new T();
    }

    private static bool TryDeserialize<T>(JsonObject node, out T value) where T : new()
    {
        try
        {
            value = node.Deserialize<T>(SettingsJson.Options) ?? new T();
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException or FormatException or ArgumentException)
        {
            value = new T();
            return false;
        }
    }

    private void KeepBackup()
    {
        try
        {
            File.Copy(Path, BackupPath, overwrite: true);
        }
        catch (IOException)
        {
            // The defaults load either way.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
