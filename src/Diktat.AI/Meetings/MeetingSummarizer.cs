using Diktat.AI.Internal;
using Diktat.Core.Text;

namespace Diktat.AI.Meetings;

/// A model that summarizes meetings, with how many characters it reads per request.
/// <param name="Name">Shown in notes, e.g. "qwen3.5:9b".</param>
public sealed record MeetingModelBackend(string Name, int CharacterLimit, Func<string, string, CancellationToken, Task<string>> Complete)
{
    /// Characters per request to a model server on this PC. Ollama's own context length decides
    /// what the model reads (OLLAMA_CONTEXT_LENGTH); 20,000 characters need about 16K tokens.
    public const int LocalServerCharacterLimit = 20_000;

    /// Characters per request for a model the app runs itself: about 11,000 tokens of German.
    public const int LocalModelCharacterLimit = 40_000;

    /// The model server on this PC (Ollama, LM Studio), or null when none is set up. Never a
    /// server elsewhere in the network or a provider on the internet.
    public static MeetingModelBackend? ServerOnThisPC(ITextRewriter rewriter)
    {
        if (rewriter.ServerOnThisPC is not { } config) return null;
        return new MeetingModelBackend(config.Model, LocalServerCharacterLimit,
            (instruction, text, cancellationToken) => rewriter.RewriteOnServerOnThisPCAsync(text, instruction, cancellationToken));
    }
}

/// Summary, decisions and to-dos for a meeting transcript.
///
/// A model reads only so many characters at once, so long meetings are summarized in parts first
/// ("map") and the notes are combined afterwards ("reduce").
public static class MeetingSummarizer
{
    public const string FinalInstruction = """
        Du erhältst das Transkript eines Meetings oder Notizen daraus. Wer spricht, ist mit Namen oder als „Person 1“, „Person 2“ markiert. Korrigiert sich jemand beim Sprechen, gilt nur die korrigierte Fassung.
        Schreibe auf Deutsch genau diese drei Abschnitte:

        Zusammenfassung
        Drei bis fünf klare Sätze: worum es ging und was herauskam.

        Entscheidungen
        Spiegelstriche mit „- “. Nur Festlegungen, auf die sich die Runde geeinigt hat, keine Aufgaben. Gab es keine, schreibe „- Keine“.

        To-dos
        Spiegelstriche im Format „- Wer: Was“. „Wer“ ist der Name oder „Person 1“, „Person 2“ wie im Transkript. Sagt jemand „mache ich“ oder „übernehme ich“, ist diese Person zuständig. Nur wenn wirklich niemand genannt ist, schreibe „Offen“. Nennt das Transkript für eine Aufgabe eine Frist, hänge sie in Klammern an, mit den Worten aus dem Transkript. Nennt es keine, lass die Klammer weg. Gab es keine Aufgaben, schreibe „- Keine“.

        Erfinde nichts dazu, vor allem keine Termine, Fristen, Namen oder Zahlen. Keine weiteren Abschnitte, keine Einleitung.
        """;

    /// The same for meetings held in another language than German: the summary follows the
    /// meeting, so the names of decisions and tasks stay as they were said.
    public const string EnglishFinalInstruction = """
        You receive the transcript of a meeting or notes taken from it. Speakers are marked by name or as "Person 1", "Person 2". When someone corrects themselves while speaking, only the corrected version counts.
        Write exactly these three sections in English:

        Summary
        Three to five clear sentences: what it was about and what came out of it.

        Decisions
        Bullet points starting with "- ". Only what the group agreed on, no tasks. If there were none, write "- None".

        To-dos
        Bullet points in the format "- Who: What". "Who" is the name or "Person 1", "Person 2" as in the transcript. Whoever says "I'll do it" or "I'll take that" is responsible. Only if really nobody is named, write "Open". If the transcript names a deadline for a task, add it in brackets, in the words of the transcript. If it names none, leave the brackets out. If there were no tasks, write "- None".

        Do not invent anything, above all no dates, deadlines, names or numbers. No other sections, no introduction.
        """;

    public const string EnglishPartInstruction = """
        You receive an excerpt from the transcript of a meeting. Speakers are marked. When someone corrects themselves while speaking, only the corrected version counts.
        Note in short bullet points: topics discussed, decisions made and tasks named with the person responsible.
        Keep names, numbers, dates and deadlines that are in the excerpt. Do not invent anything. Only the bullet points, no introduction.
        """;

    public const string PartInstruction = """
        Du erhältst einen Ausschnitt aus dem Transkript eines Meetings. Wer spricht, ist markiert. Korrigiert sich jemand beim Sprechen, gilt nur die korrigierte Fassung.
        Halte in knappen Stichpunkten fest: besprochene Themen, getroffene Entscheidungen und genannte Aufgaben mit der zuständigen Person.
        Behalte Namen, Zahlen, Termine und Fristen, die im Ausschnitt stehen. Erfinde nichts dazu. Nur die Stichpunkte, keine Einleitung.
        """;

    /// Summarizes with the given model. The instructions are sent as they are, without the
    /// dictation rules: their examples name weekdays the model copied into to-dos, and the
    /// self-correction rule is in the instructions above.
    public static async Task<string> SummarizeAsync(string transcript, MeetingModelBackend backend, CancellationToken cancellationToken = default)
    {
        var limit = backend.CharacterLimit;
        var isGerman = IsGermanMeeting(transcript);
        var material = transcript;
        var round = 0;
        while (material.Length > limit && round < 3)
        {
            var notes = new List<string>();
            foreach (var part in Chunks(material, limit))
            {
                notes.Add(await backend.Complete(isGerman ? PartInstruction : EnglishPartInstruction, part, cancellationToken).ConfigureAwait(false));
            }
            material = string.Join("\n\n", notes);
            round++;
        }
        var summary = await backend.Complete(isGerman ? FinalInstruction : EnglishFinalInstruction, material, cancellationToken).ConfigureAwait(false);
        var withoutInvented = InventedDeadlineFilter.Filtered(Cleaned(summary), transcript, isGerman);
        return DecisionTodoFilter.Filtered(withoutInvented);
    }

    /// German unless the transcript is clearly in another language. Too little text to tell
    /// counts as German, the language of the app.
    public static bool IsGermanMeeting(string transcript) => PromptLanguages.Detect(transcript) == PromptLanguage.German;

    private static readonly string[] Echoes =
    [
        "Erfinde nichts dazu", "Keine weiteren Abschnitte", "Spiegelstriche im Format", "Drei bis fünf klare Sätze", "lass die Klammer weg",
        "Spiegelstriche mit „- “:",
        "Do not invent anything", "No other sections", "Bullet points in the format", "Three to five clear sentences", "leave the brackets out",
        "Bullet points starting with \"- \":",
    ];

    private static readonly string[] Placeholders = ["- Offen", "- Offen:", "- Open", "- Open:"];

    /// Small models sometimes echo parts of the instruction or leave placeholder bullets.
    public static string Cleaned(string summary)
    {
        var lines = summary.Split(TextHelpers.Newlines)
            .Select(line => TextHelpers.Trim(line, TextHelpers.IsHorizontalWhitespace))
            .Where(line => !Echoes.Any(line.Contains) && !Placeholders.Contains(line));
        return string.Join("\n", lines).Replace("\n\n\n", "\n\n").Trim();
    }

    /// Splits at paragraph boundaries (speaker turns) so no turn is cut in half.
    public static List<string> Chunks(string text, int limit)
    {
        var result = new List<string>();
        var current = "";
        foreach (var paragraph in text.Split("\n\n"))
        {
            if (current.Length > 0 && current.Length + paragraph.Length + 2 > limit)
            {
                result.Add(current);
                current = "";
            }
            if (paragraph.Length > limit)
            {
                // A single very long turn: cut at sentence ends.
                var remaining = paragraph;
                while (remaining.Length > limit)
                {
                    var sentenceEnd = remaining[..limit].LastIndexOfAny(['.', '!', '?']);
                    var end = sentenceEnd >= 0 ? sentenceEnd + 1 : limit;
                    result.Add(remaining[..end]);
                    remaining = remaining[end..];
                }
                current = remaining;
            }
            else
            {
                current += current.Length == 0 ? paragraph : "\n\n" + paragraph;
            }
        }
        if (current.Length > 0) result.Add(current);
        return result;
    }
}

/// The outcome of summarizing a meeting: a summary, a note why it is missing or how it was made,
/// or both. A failure keeps an earlier summary.
public sealed record MeetingSummaryResult(string? Summary, string? Note);

/// Summarizes a stored meeting on this PC only (MeetingController.summarize on the Mac).
/// Order: a model the app runs itself when given (unless the server is preferred), then the model
/// server on this PC (Ollama, LM Studio). Meetings never go to a server elsewhere or to a model
/// provider on the internet, whatever the engine setting.
public sealed class MeetingSummaryService
{
    public static string NoModelNote => L.T("Für die Zusammenfassung braucht Diktat ein Modell auf diesem PC, etwa Ollama oder LM Studio. Meetings gehen nie an einen Modell-Anbieter im Internet.");

    private readonly ITextRewriter _rewriter;

    public MeetingSummaryService(ITextRewriter rewriter)
    {
        _rewriter = rewriter;
    }

    /// Whether a model on this PC can summarize: the model server, or a local model the app brings.
    public bool IsAvailable(MeetingModelBackend? localModel = null) => localModel is not null || _rewriter.ServerOnThisPC is not null;

    /// <param name="ownName">The first name of the Windows account, so tasks the person at this PC
    /// took on are assigned to them; null without one.</param>
    /// <param name="localModel">A model the app runs itself, if any.</param>
    /// <param name="prefersServerOnThisPC">"Immer Modell-Anbieter": the server on this PC comes first.</param>
    public async Task<MeetingSummaryResult> SummarizeAsync(MeetingRecord meeting, string? ownName = null,
        MeetingModelBackend? localModel = null, bool prefersServerOnThisPC = false, CancellationToken cancellationToken = default)
    {
        var transcript = meeting.TranscriptText(ownName);
        var server = MeetingModelBackend.ServerOnThisPC(_rewriter);
        string? failure = null;

        if (prefersServerOnThisPC && server is not null)
        {
            var (summary, serverFailure) = await TryAsync(server, transcript, isServer: true, cancellationToken).ConfigureAwait(false);
            if (summary is not null) return new MeetingSummaryResult(summary, null);
            failure = serverFailure;
            server = null;
        }
        if (localModel is not null)
        {
            var (summary, localFailure) = await TryAsync(localModel, transcript, isServer: false, cancellationToken).ConfigureAwait(false);
            if (summary is not null) return new MeetingSummaryResult(summary, null);
            failure = Join(failure, localFailure);
        }
        if (server is not null)
        {
            var (summary, serverFailure) = await TryAsync(server, transcript, isServer: true, cancellationToken).ConfigureAwait(false);
            if (summary is not null) return new MeetingSummaryResult(summary, null);
            failure = Join(failure, serverFailure);
        }
        // A failure keeps an earlier summary; without any model the summary goes.
        return failure is not null
            ? new MeetingSummaryResult(meeting.Summary, failure)
            : new MeetingSummaryResult(null, NoModelNote);
    }

    private static async Task<(string? Summary, string? Failure)> TryAsync(MeetingModelBackend backend, string transcript, bool isServer,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await MeetingSummarizer.SummarizeAsync(transcript, backend, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return (null, isServer
                ? L.F("{0} auf diesem PC konnte nicht zusammenfassen ({1}). Läuft Ollama oder LM Studio?", backend.Name, e.Message)
                : L.F("{0} konnte nicht zusammenfassen ({1}).", backend.Name, e.Message));
        }
    }

    private static string? Join(string? first, string? second) =>
        first is null ? second : second is null ? first : first + " " + second;
}
