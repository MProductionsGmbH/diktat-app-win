using Diktat.AI.Meetings;

namespace Diktat.AI.Tests;

public sealed class DecisionTodoFilterTests
{
    private static string Summary(string[] decisions, string[] todos, bool english = false)
    {
        var (summaryHead, decisionsHead) = english ? ("Summary", "Decisions") : ("Zusammenfassung", "Entscheidungen");
        return $"{summaryHead}\nEs ging um den Workshop.\n\n{decisionsHead}\n"
            + string.Join("\n", decisions.Select(d => "- " + d))
            + "\n\nTo-dos\n"
            + string.Join("\n", todos.Select(t => "- " + t));
    }

    /// The results of Apple's model on the test meeting "Workshop", as they came out.
    [Fact]
    public void TaskListedAsDecisionIsRemovedAndTheRealDecisionStays()
    {
        var input = Summary(
            ["Den Raum buchen", "Online statt im Büro machen"],
            ["Person 2: Den Raum buchen", "Person 1: Die Einladung verschicken (bis Freitag)"]);
        var result = DecisionTodoFilter.Filtered(input);
        Assert.DoesNotContain("- Den Raum buchen", result);
        Assert.Contains("- Online statt im Büro machen", result);
        Assert.Contains("- Person 2: Den Raum buchen", result); // the to-dos are never touched
    }

    [Fact]
    public void AllDecisionsRepeatingTodosLeaveNone()
    {
        var input = Summary(["Raum buchen", "Agenda und Unterlagen senden"], ["Person 2: Raum buchen", "Person 1: Agenda und Unterlagen senden"]);
        Assert.Contains("Entscheidungen\n- Keine\n", DecisionTodoFilter.Filtered(input));
    }

    [Fact]
    public void EnglishSummaryGetsAnEnglishPlaceholder()
    {
        var input = Summary(["Sign the contract"], ["Person 2: Sign the contract (Wednesday)"], english: true);
        Assert.Contains("Decisions\n- None\n", DecisionTodoFilter.Filtered(input));
    }

    [Fact]
    public void StemmingMeetsGeklaertAndKlaeren()
    {
        var input = Summary(
            ["Launch am 12. März", "Preise werden mit dem Vertrieb geklärt", "Landing-Page wird fertiggestellt"],
            ["Person 3: Preise mit dem Vertrieb klären", "Person 1: Landing-Page fertigstellen (bis zum 5. März)"]);
        var result = DecisionTodoFilter.Filtered(input);
        Assert.Contains("- Launch am 12. März", result);
        Assert.DoesNotContain("- Preise werden mit dem Vertrieb geklärt", result);
        Assert.DoesNotContain("- Landing-Page wird fertiggestellt", result);
    }

    [Fact]
    public void ADecisionWithANumberTheTodoLacksStays()
    {
        var input = Summary(["Preise um 5 % erhöhen"], ["Person 1: Preise prüfen"]);
        Assert.Contains("- Preise um 5 % erhöhen", DecisionTodoFilter.Filtered(input));
    }

    [Fact]
    public void ADecisionThatSharesOnlyAWordStays()
    {
        var input = Summary(["Der Launch findet im März statt"], ["Person 1: Launch vorbereiten"]);
        Assert.Contains("- Der Launch findet im März statt", DecisionTodoFilter.Filtered(input));
    }

    [Fact]
    public void WithoutTodosNothingChanges()
    {
        const string input = "Zusammenfassung\nKurz.\n\nEntscheidungen\n- Raum buchen\n\nTo-dos\n- Keine";
        Assert.Equal(input, DecisionTodoFilter.Filtered(input));
    }

    [Fact]
    public void ItDoesNotChangeAnAlreadyCleanSummaryAndIsIdempotent()
    {
        var input = Summary(["Das Treffen ist am Donnerstag um zehn"], ["Person 2: Raum buchen", "Person 1: Unterlagen schicken (bis morgen)"]);
        Assert.Equal(input, DecisionTodoFilter.Filtered(input));
        var first = DecisionTodoFilter.Filtered(Summary(["Raum buchen"], ["Person 2: Raum buchen"]));
        Assert.Equal(first, DecisionTodoFilter.Filtered(first));
    }

    [Fact]
    public void NoneInTheDecisionsStaysNone()
    {
        var input = Summary(["Keine"], ["Person 1: Raum buchen"]);
        Assert.Equal(input, DecisionTodoFilter.Filtered(input));
    }
}

public sealed class InventedDeadlineFilterTests
{
    private const string Summary = """
        Zusammenfassung
        Das Team plant den Workshop.

        Entscheidungen
        - Der Workshop findet online statt (statt im Büro)

        To-dos
        - Person 1: Agenda schreiben (bis Freitag)
        - Person 2: Raum buchen (bis Montag)
        - Offen: Teilnehmerliste klären
        """;

    private const string Transcript = "Person 1: Ich schreibe die Agenda bis Freitag.\n\nPerson 2: Ich buche den Raum.";

    private static string Filtered(string summary, string transcript, bool german = true) =>
        InventedDeadlineFilter.Filtered(summary, transcript, german);

    [Fact]
    public void KeepsDeadlineNamedInTranscript() =>
        Assert.Contains("- Person 1: Agenda schreiben (bis Freitag)", Filtered(Summary, Transcript));

    [Fact]
    public void RemovesDeadlineNobodyNamed()
    {
        var result = Filtered(Summary, Transcript);
        Assert.Contains("- Person 2: Raum buchen\n", result);
        Assert.DoesNotContain("Montag", result);
    }

    [Fact]
    public void LeavesOtherSectionsAlone()
    {
        var result = Filtered(Summary, "Wir machen den Workshop online.");
        Assert.Contains("- Der Workshop findet online statt (statt im Büro)", result);
        Assert.Contains("- Offen: Teilnehmerliste klären", result);
    }

    [Fact]
    public void LeavesSummaryOfOtherLanguagesAlone()
    {
        // The German summary says "Freitag" for a spoken "Friday".
        Assert.Equal(Summary, Filtered(Summary, "Person 1: I'll send the agenda by Friday.", german: false));
    }

    [Fact]
    public void KeepsPhrasedDeadlineWhenItsWordsWereSaid()
    {
        const string todo = "To-dos\n- Anna: Angebot senden (bis Ende der Woche)";
        Assert.Equal(todo, Filtered(todo, "Anna schickt das Angebot bis Ende der Woche."));
        Assert.Equal("To-dos\n- Anna: Angebot senden", Filtered(todo, "Anna schickt das Angebot."));
    }

    [Fact]
    public void ComparesWholeWordsNotParts()
    {
        const string todo = "To-dos\n- Anna: Angebot senden (bis Ende Oktober)";
        // "ende" hides in "Kalender" and "senden", but nobody said "Ende" or "Oktober".
        Assert.Equal("To-dos\n- Anna: Angebot senden", Filtered(todo, "Bitte den Kalender senden."));
        // A longer spoken form still counts.
        const string day = "To-dos\n- Anna: Bericht (bis Freitag)";
        Assert.Equal(day, Filtered(day, "Das mache ich Freitagabend."));
    }

    [Fact]
    public void KeepsDeadlinesThatAreOnlyNumbers()
    {
        // "bis zum dreiundzwanzigsten" can come back as "23.", so numbers are not checked.
        const string todo = "**To-dos:**\n- Person 1: Bericht (bis 23.)";
        Assert.Equal(todo, Filtered(todo, "Den Bericht schreibe ich bis zum dreiundzwanzigsten."));
        const string month = "To-dos\n- Person 1: Bericht (bis 15. November)";
        Assert.Equal("To-dos\n- Person 1: Bericht", Filtered(month, "Den Bericht schreibe ich."));
    }

    [Fact]
    public void HandlesOtherBulletsAndTrailingPunctuation()
    {
        const string todos = "To-dos\n• Person 1: Raum buchen (bis Montag).\n* Person 2: Agenda (bis Montag)\n1. Anna: Einladung (bis Montag);";
        Assert.Equal(
            "To-dos\n• Person 1: Raum buchen\n* Person 2: Agenda\n1. Anna: Einladung",
            Filtered(todos, "Wir buchen den Raum und schicken die Einladung."));
    }
}
