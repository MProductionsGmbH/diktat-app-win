using Diktat.AI.Meetings;
using Diktat.Core.Text;

namespace Diktat.AI.Tests;

/// The tests switch SwissSpelling.IsEnabled on, so they run alone.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SwissSpellingCollection
{
    public const string Name = "Swiss spelling";
}

[Collection(SwissSpellingCollection.Name)]
public sealed class MeetingSwissSpellingTests
{
    [Fact]
    public async Task SummariesFollowTheSetting()
    {
        var backend = new MeetingModelBackend("test", 60_000, (_, _, _) =>
            Task.FromResult("Zusammenfassung\nDie Strasse wird größer gebaut.\n\nEntscheidungen\n- Wir schließen den Vertrag ab."));
        const string transcript = "Person 1: Wir bauen die Straße größer.\n\nPerson 2: Und wir schließen den Vertrag ab.";
        try
        {
            SwissSpelling.IsEnabled = true;
            var summary = await MeetingSummarizer.SummarizeAsync(transcript, backend);
            Assert.DoesNotContain("ß", summary);
            Assert.Contains("Die Strasse wird grösser gebaut.", summary);
            Assert.Contains("- Wir schliessen den Vertrag ab.", summary);

            SwissSpelling.IsEnabled = false;
            Assert.Contains("größer", await MeetingSummarizer.SummarizeAsync(transcript, backend));
        }
        finally
        {
            SwissSpelling.IsEnabled = false;
        }
    }

    [Fact]
    public async Task LiveNotesAndTheCatchUpFollowTheSetting()
    {
        var backend = new LiveNotesBackend("test", 20_000, (instruction, _, _) =>
            Task.FromResult(instruction == LiveNotes.CatchUpInstruction ? "Es ging um die Straße." : "Themen\n- Die Straße"));
        var notes = new LiveNotes(_ => Task.FromResult(backend));
        try
        {
            SwissSpelling.IsEnabled = true;
            await notes.UpdateAsync(_ => Task.FromResult("Ich: Wir reden über die Straße."));
            Assert.Equal("Themen\n- Die Strasse", notes.Notes);
            await notes.CatchUpOnLastMinutesAsync();
            Assert.Equal("Es ging um die Strasse.", notes.CatchUp);
        }
        finally
        {
            SwissSpelling.IsEnabled = false;
            await notes.StopAsync();
        }
    }
}
