using System.Text.RegularExpressions;
using Diktat.AI.Internal;

namespace Diktat.AI.Meetings;

/// Removes decisions that only repeat a to-do. Small models like to list "Den Raum buchen" under
/// "Entscheidungen" although somebody just took it on as a task. A decision goes when most of its
/// words are in one to-do; when in doubt it stays. A decision with a number or date that the to-do
/// does not have also stays ("Preise um 5 % erhöhen" is more than the to-do "Preise prüfen").
public static class DecisionTodoFilter
{
    private static readonly HashSet<string> StopWords =
    [
        // German
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einer", "eines", "einen", "einem", "und", "oder",
        "mit", "zur", "zum", "von", "vom", "wird", "werden", "wurde", "wurden", "ist", "sind", "war", "nicht", "auch",
        "dass", "bei", "für", "auf", "aus", "nach", "über", "unter", "bis", "wir", "sie", "man", "dann", "noch", "nur",
        "person", "soll", "sollen",
        // English
        "the", "and", "are", "was", "were", "will", "with", "for", "from", "that", "this", "then", "also", "not",
        "they", "should", "shall", "have", "has", "been",
    ];

    private enum Section { Summary, Decisions, Todos }

    private static readonly Dictionary<string, Section> Sections = new()
    {
        ["zusammenfassung"] = Section.Summary, ["summary"] = Section.Summary,
        ["entscheidungen"] = Section.Decisions, ["decisions"] = Section.Decisions,
        ["to-dos"] = Section.Todos, ["todos"] = Section.Todos, ["to-do"] = Section.Todos,
    };

    private static readonly Regex Bullet = new(@"^\s*(?:[-*•–]|\d+[.)])\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Parenthesis = new(@"\([^()]*\)", RegexOptions.CultureInvariant);

    private static readonly string[] Endings = ["ungen", "ung", "ern", "en", "et", "st", "te", "t", "e", "n", "s"];

    public static string Filtered(string summary)
    {
        var lines = summary.Split('\n');

        Section? section = null;
        var todos = new List<HashSet<string>>();
        foreach (var line in lines)
        {
            if (Heading(line) is { } found) section = found;
            else if (section == Section.Todos && BulletText(line) is { } text) todos.Add(Words(TodoTask(text)));
        }
        if (todos.Count == 0) return summary;

        var result = new List<string>();
        var inDecisions = false;
        var germanHeading = true;
        var kept = 0;
        var dropped = 0;
        int? placeholderAt = null;

        void FlushDecisions()
        {
            if (inDecisions && kept == 0 && dropped > 0 && placeholderAt is int index)
            {
                result.Insert(index, germanHeading ? "- Keine" : "- None");
            }
            inDecisions = false;
            kept = 0;
            dropped = 0;
            placeholderAt = null;
        }

        section = null;
        foreach (var line in lines)
        {
            if (Heading(line) is { } found)
            {
                FlushDecisions();
                section = found;
                if (found == Section.Decisions)
                {
                    inDecisions = true;
                    germanHeading = TextHelpers.BareHeading(line) == "entscheidungen";
                }
                result.Add(line);
                continue;
            }
            if (section == Section.Decisions && BulletText(line) is { } text)
            {
                if (IsNone(text))
                {
                    kept++;
                }
                else if (RepeatsATodo(text, todos))
                {
                    dropped++;
                    placeholderAt ??= result.Count;
                    continue;
                }
                else
                {
                    kept++;
                }
            }
            result.Add(line);
        }
        FlushDecisions();
        return string.Join("\n", result);
    }

    private static Section? Heading(string line) =>
        Sections.TryGetValue(TextHelpers.BareHeading(line), out var section) ? section : null;

    private static string? BulletText(string line)
    {
        var match = Bullet.Match(line);
        if (!match.Success) return null;
        return TextHelpers.Trim(line[(match.Index + match.Length)..], TextHelpers.IsHorizontalWhitespace);
    }

    private static bool IsNone(string text) =>
        TextHelpers.Trim(text.ToLowerInvariant(), c => c == '.') is "keine" or "none" or "noch keine" or "none yet";

    /// "Person 2: Den Raum buchen (bis Freitag)" -> "Den Raum buchen".
    private static string TodoTask(string text)
    {
        var task = text;
        var colon = task.IndexOf(':');
        if (colon >= 0 && colon <= 30) task = task[(colon + 1)..];
        return Parenthesis.Replace(task, " ");
    }

    private static bool RepeatsATodo(string decision, List<HashSet<string>> todos)
    {
        var decisionWords = Words(decision);
        if (decisionWords.Count == 0) return false;
        var numbers = Numbers(decision);
        return todos.Any(todo =>
        {
            if (todo.Count == 0) return false;
            var shared = decisionWords.Count(todo.Contains);
            if ((double)shared / decisionWords.Count < 0.6) return false;
            return numbers.IsSubsetOf(todo);
        });
    }

    /// Digits as they are said ("12", "5"): a date or an amount that only the decision names.
    private static HashSet<string> Numbers(string text) =>
        TextHelpers.SplitKeeping(text, char.IsDigit).Select(n => "#" + n).ToHashSet();

    /// Significant words, lightly stemmed so "geklärt" and "klären" meet: no stop words, no words
    /// under three letters, endings stripped once, then the first six letters. Numbers count as
    /// words ("#12").
    private static HashSet<string> Words(string text)
    {
        var result = Numbers(text);
        foreach (var raw in TextHelpers.SplitKeeping(text.ToLowerInvariant(), TextHelpers.IsLetter))
        {
            if (raw.Length < 3 || StopWords.Contains(raw)) continue;
            var word = raw;
            foreach (var ending in Endings)
            {
                if (word.EndsWith(ending, StringComparison.Ordinal) && word.Length - ending.Length >= 4)
                {
                    word = word[..^ending.Length];
                    break;
                }
            }
            result.Add(word.Length > 6 ? word[..6] : word);
        }
        return result;
    }
}
