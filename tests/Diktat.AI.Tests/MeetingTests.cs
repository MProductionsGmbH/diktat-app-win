using System.Text;
using Diktat.AI.Meetings;
using Diktat.Core.Support;

namespace Diktat.AI.Tests;

public sealed class MeetingSummarizerTests
{
    private const string Answer = """
        Zusammenfassung
        Das Team plant den Workshop.

        Entscheidungen
        - Den Raum buchen
        - Online statt im Büro

        To-dos
        - Person 2: Den Raum buchen (bis Montag)
        - Offen
        Erfinde nichts dazu, vor allem keine Termine.
        """;

    [Fact]
    public async Task SummaryIsCleanedAndFiltered()
    {
        var calls = new List<string>();
        var backend = new MeetingModelBackend("test", 60_000, (instruction, text, _) =>
        {
            calls.Add(instruction);
            return Task.FromResult(Answer);
        });
        var summary = await MeetingSummarizer.SummarizeAsync("Person 1: Wir machen das online.\n\nPerson 2: Ich buche den Raum.", backend);
        Assert.Equal(MeetingSummarizer.FinalInstruction, Assert.Single(calls));
        Assert.Equal("""
            Zusammenfassung
            Das Team plant den Workshop.

            Entscheidungen
            - Online statt im Büro

            To-dos
            - Person 2: Den Raum buchen
            """, summary);
    }

    [Fact]
    public async Task LongMeetingsAreSummarizedInParts()
    {
        var calls = new List<(string Instruction, string Text)>();
        var backend = new MeetingModelBackend("test", 100, (instruction, text, _) =>
        {
            calls.Add((instruction, text));
            return Task.FromResult(instruction == MeetingSummarizer.PartInstruction ? "- kurz" : "Zusammenfassung\nKurz.");
        });
        var transcript = string.Join("\n\n", Enumerable.Range(1, 6).Select(i => $"Person {i % 2 + 1}: Das ist der Beitrag Nummer {i} und er ist lang genug."));
        await MeetingSummarizer.SummarizeAsync(transcript, backend);
        Assert.True(calls.Count(c => c.Instruction == MeetingSummarizer.PartInstruction) >= 3);
        Assert.Equal(MeetingSummarizer.FinalInstruction, calls[^1].Instruction);
        Assert.All(calls, c => Assert.True(c.Text.Length <= 100));
    }

    [Fact]
    public void EnglishMeetingsGetEnglishInstructions()
    {
        Assert.False(MeetingSummarizer.IsGermanMeeting("Person 1: I think we should move the launch to next week and tell the team today."));
        Assert.True(MeetingSummarizer.IsGermanMeeting("Person 1: Ich glaube, wir sollten den Launch auf nächste Woche schieben."));
        Assert.True(MeetingSummarizer.IsGermanMeeting(""));
    }

    [Fact]
    public void ChunksCutLongTurnsAtSentenceEnds()
    {
        var chunks = MeetingSummarizer.Chunks("Erster Satz. Zweiter Satz. Dritter.\n\nKurz.", 20);
        Assert.Equal(["Erster Satz.", " Zweiter Satz.", " Dritter.\n\nKurz."], chunks);
    }
}

public sealed class MeetingSummaryServiceTests
{
    private static MeetingRecord Record() => new()
    {
        Title = "Meeting",
        Turns = [new MeetingTurn(0, 0, "Ich buche den Raum."), new MeetingTurn(1, 3, "Danke dir.")],
        Summary = "alt",
    };

    [Fact]
    public async Task WithoutAServerOnThisPCMeetingsAreNotSummarized()
    {
        var rewriter = new FakeRewriter { ServerOnThisPC = null };
        var result = await new MeetingSummaryService(rewriter).SummarizeAsync(Record());
        Assert.Null(result.Summary);
        Assert.Equal(MeetingSummaryService.NoModelNote, result.Note);
        Assert.Empty(rewriter.Calls);
    }

    [Fact]
    public async Task TheServerOnThisPCSummarizesWithTheOwnName()
    {
        var rewriter = new FakeRewriter((_, _) => "Zusammenfassung\nRaum gebucht.") { ServerOnThisPC = new AIProviderConfig(AIProvider.Custom) };
        var result = await new MeetingSummaryService(rewriter).SummarizeAsync(Record(), ownName: "Paul");
        Assert.Equal("Zusammenfassung\nRaum gebucht.", result.Summary);
        Assert.Null(result.Note);
        Assert.Equal("Paul: Ich buche den Raum.\n\nPerson 1: Danke dir.", rewriter.Calls[0].Text);
        Assert.False(rewriter.Calls[0].DictationRules);
    }

    [Fact]
    public async Task AFailureKeepsTheEarlierSummary()
    {
        var rewriter = new FakeRewriter((_, _) => throw new AIClientTestException()) { ServerOnThisPC = new AIProviderConfig(AIProvider.Custom) };
        var result = await new MeetingSummaryService(rewriter).SummarizeAsync(Record());
        Assert.Equal("alt", result.Summary);
        Assert.Equal("llama3.2 auf diesem PC konnte nicht zusammenfassen (weg). Läuft Ollama oder LM Studio?", result.Note);
    }

    private sealed class AIClientTestException() : Exception("weg");
}

public sealed class MeetingStoreTests
{
    /// Flips every byte, so the test sees that the store encrypts.
    private sealed class FlippingProtector : IDataProtector
    {
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)~b).ToArray();
        public byte[] Unprotect(byte[] protectedData) => Protect(protectedData);
    }

    [Fact]
    public void SavesEncryptedAndReadsBack()
    {
        using var directory = new TempDirectory();
        var store = new MeetingStore(directory.Path, new FlippingProtector());
        var meeting = new MeetingRecord { Title = "Workshop", Duration = 61, Status = MeetingStatus.Done };
        meeting.Turns.Add(new MeetingTurn(1, 0.5, "Hallo zusammen."));
        meeting.SpeakerNames[1] = "Anna";
        Assert.True(store.Upsert(meeting));

        var raw = File.ReadAllBytes(Path.Combine(directory.Path, MeetingStore.FileName));
        Assert.DoesNotContain("Workshop", Encoding.UTF8.GetString(raw));

        var reloaded = new MeetingStore(directory.Path, new FlippingProtector()).Meeting(meeting.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Workshop", reloaded.Title);
        Assert.Equal(MeetingStatus.Done, reloaded.Status);
        Assert.Equal("Anna: Hallo zusammen.", reloaded.TranscriptPlainText);
    }

    [Fact]
    public void RecordsAreCopiesAndNewestComesFirst()
    {
        using var directory = new TempDirectory();
        var store = new MeetingStore(directory.Path, new PassThroughDataProtector());
        var first = new MeetingRecord { Title = "Eins" };
        store.Upsert(first);
        first.Title = "geändert";
        Assert.Equal("Eins", store.Meeting(first.Id)!.Title);
        store.Upsert(new MeetingRecord { Title = "Zwei" });
        Assert.Equal(["Zwei", "Eins"], store.Meetings.Select(m => m.Title));

        store.SetSummary(first.Id, "Kurz.", null);
        Assert.Equal("Kurz.", store.Meeting(first.Id)!.Summary);
        store.Delete(first.Id);
        Assert.Null(store.Meeting(first.Id));
        Assert.Single(store.Meetings);
    }

    [Fact]
    public void UnreadableFileIsKeptAside()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, MeetingStore.FileName), "kaputt");
        var store = new MeetingStore(directory.Path, new PassThroughDataProtector());
        Assert.Empty(store.Meetings);
        Assert.Single(Directory.GetFiles(directory.Path, ".meetings.unreadable-*"));
    }

    [Fact]
    public void OldSpeakerLabelsAreMigrated()
    {
        using var directory = new TempDirectory();
        new MeetingStore(directory.Path, new PassThroughDataProtector()).Upsert(new MeetingRecord { Summary = "- Sprecher 2: Raum buchen" });
        var store = new MeetingStore(directory.Path, new PassThroughDataProtector());
        Assert.Equal("- Person 2: Raum buchen", store.Meetings[0].Summary);
    }

    [Fact]
    public void NamesAndTexts()
    {
        var meeting = new MeetingRecord
        {
            Title = MeetingRecord.TitleFor(new DateTimeOffset(2026, 10, 17, 11, 58, 0, TimeSpan.Zero)),
            Turns = [new MeetingTurn(0, 0, "Hallo."), new MeetingTurn(2, 1, "Hi.")],
            Summary = "Kurz.",
        };
        Assert.Equal("Meeting am 17. Okt. 2026, 11:58", meeting.Title);
        Assert.Equal("Ich", meeting.NameFor(0));
        Assert.Equal("Person 2", meeting.NameFor(2));
        Assert.Equal([0, 2], meeting.Speakers);
        Assert.Equal("Paul: Hallo.\n\nPerson 2: Hi.", meeting.TranscriptText("Paul"));
        Assert.Equal("Meeting am 17. Okt. 2026, 11:58\n\nKurz.\n\nTranskript\n\nIch: Hallo.\n\nPerson 2: Hi.", meeting.ShareText);
        meeting.SpeakerNames[0] = "Marc";
        Assert.Equal("Marc: Hallo.\n\nPerson 2: Hi.", meeting.TranscriptText("Paul"));
    }
}

public sealed class LiveNotesTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task NotesGrowWithEachStretch()
    {
        var inputs = new List<string>();
        var backend = new LiveNotesBackend("test", 20_000, (instruction, text, _) =>
        {
            inputs.Add(text);
            return Task.FromResult($"Themen\n- Punkt {inputs.Count}");
        });
        var notes = new LiveNotes(_ => Task.FromResult(backend));

        await notes.UpdateAsync(_ => Task.FromResult("Ich: Wir fangen mit den Zahlen an."));
        Assert.Equal("Themen\n- Punkt 1", notes.Notes);
        Assert.Equal("Neuer Abschnitt:\nIch: Wir fangen mit den Zahlen an.", inputs[0]);
        Assert.Equal("test", notes.ModelName);
        Assert.Null(notes.Problem);

        notes.AddStretch("Andere: Die liegen seit gestern vor.");
        await notes.UpdateAsync();
        Assert.Equal("Bisherige Notizen:\nThemen\n- Punkt 1\n\nNeuer Abschnitt:\nAndere: Die liegen seit gestern vor.", inputs[1]);
        await notes.StopAsync();
    }

    [Fact]
    public async Task TextTheModelFailsOnTwiceIsLeftOut()
    {
        var fail = true;
        var backend = new LiveNotesBackend("test", 20_000, (_, _, _) =>
            fail ? throw new InvalidOperationException("Fehler") : Task.FromResult("Themen\n- ok"));
        var notes = new LiveNotes(_ => Task.FromResult(backend));

        notes.AddStretch("Ich: Erster Teil.");
        await notes.UpdateAsync();
        Assert.Equal("Live-Notizen gerade nicht möglich: Fehler", notes.Problem);

        await notes.UpdateAsync();
        Assert.Equal("Ein Abschnitt ließ sich nicht in die Notizen übernehmen. Im Transkript nach dem Meeting fehlt nichts.", notes.Problem);

        fail = false;
        notes.AddStretch("Ich: Zweiter Teil.");
        await notes.UpdateAsync();
        Assert.Null(notes.Problem);
        Assert.Equal("Themen\n- ok", notes.Notes);
    }

    [Fact]
    public async Task CatchUpReadsOnlyTheLastMinutes()
    {
        var time = new ManualTime();
        string? read = null;
        var backend = new LiveNotesBackend("test", 20_000, (instruction, text, _) =>
        {
            read = text;
            return Task.FromResult("Kurz gesagt.");
        });
        var notes = new LiveNotes(_ => Task.FromResult(backend), time);
        await notes.CatchUpOnLastMinutesAsync();
        Assert.Equal("Noch nichts aufgeschrieben. Die ersten Notizen kommen nach etwa zweieinhalb Minuten.", notes.CatchUp);

        notes.AddStretch("Ich: Alt.");
        time.Now = time.Now.AddMinutes(20);
        notes.AddStretch("Andere: Neu.");
        await notes.CatchUpOnLastMinutesAsync();
        Assert.Equal("Andere: Neu.", read);
        Assert.Equal("Kurz gesagt.", notes.CatchUp);
    }

    [Fact]
    public void StretchShowsWhoSpoke()
    {
        TimedWord[] own = [new("Ja,", 2.0, 2.2), new("gerne.", 2.3, 2.6)];
        TimedWord[] others = [new("Kommst", 0.0, 0.3), new("du?", 0.4, 0.6)];
        Assert.Equal("Andere: Kommst du?\nPaul: Ja, gerne.", LiveNotes.ComposeStretch(own, others, "Paul"));
        Assert.Equal("Ja, gerne.", LiveNotes.ComposeStretch(own, [], null));
    }

    [Fact]
    public void ServerBackendOnlyForAServerOnThisPC()
    {
        Assert.Null(LiveNotesBackend.ServerOnThisPC(new FakeRewriter()));
        var backend = LiveNotesBackend.ServerOnThisPC(new FakeRewriter { ServerOnThisPC = new AIProviderConfig(AIProvider.Custom, model: "qwen3.5:9b") });
        Assert.Equal("qwen3.5:9b (Ollama)", backend!.Name);
        Assert.Equal(20_000, backend.CharacterLimit);
    }
}
