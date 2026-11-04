using Diktat.Core.Text;

namespace Diktat.Core.Tests;

public class TranscriptParagraphServiceTests
{
    private static readonly string[] Sentences =
    [
        "Heute schreiben wir einen Brief.",
        "Dieser Brief enthält wichtige Informationen.",
        "Wir schicken ihn morgen weiter.",
        "Die Antwort erwarten wir nächste Woche.",
        "Danach planen wir die nächsten Schritte.",
    ];

    private static string FirstFour => string.Join(" ", Sentences.Take(4));
    private static string Input => string.Join(" ", Sentences);
    private static string Expected => FirstFour + "\n\n" + Sentences[4];

    private static string Format(string text) => TranscriptParagraphService.Format(text, "de");

    [Fact]
    public void BreaksAfterFourSubstantialSentences()
    {
        Assert.Equal(Expected, Format(Input));
        Assert.Equal(FirstFour, Format(FirstFour));
    }

    [Fact]
    public void InterjectionsDoNotCount() =>
        Assert.Equal("Ja. OK. Nein. Gut. " + Expected, Format("Ja. OK. Nein. Gut. " + Input));

    [Fact]
    public void WordCapEndsAtSentenceBoundaryOnly()
    {
        var longSentence = string.Join(" ", Enumerable.Repeat("Wort", 46)) + ".";
        Assert.Equal(longSentence + "\n\nDanach folgt mehr Text.", Format(longSentence + " Danach folgt mehr Text."));
        Assert.Equal(longSentence, Format(longSentence));
    }

    [Fact]
    public void LeavesShortAndEmptyTextAlone()
    {
        Assert.Equal("Hallo 👋, das ist ein Test!", Format("Hallo 👋, das ist ein Test!"));
        Assert.Equal("", TranscriptParagraphService.Format(""));
        Assert.Equal("   \n", TranscriptParagraphService.Format("   \n"));
    }

    [Fact]
    public void KeepsExistingParagraphsAndIsIdempotent()
    {
        var existing = string.Join(" ", Sentences.Take(2)) + "\r\n \r\n" + string.Join(" ", Sentences.Skip(2));
        Assert.Equal(existing, Format(existing));
        Assert.Equal(Expected, Format(Expected));
    }

    [Fact]
    public void GermanAbbreviation()
    {
        var abbreviated = string.Join(" ", new[] { "Dr. Müller schreibt heute einen Brief." }.Concat(Sentences.Take(3)));
        Assert.Equal(abbreviated + "\n\n" + Sentences[4], Format(abbreviated + " " + Sentences[4]));
    }

    [Fact]
    public void PreservesWordsAndPunctuation()
    {
        var large = string.Join(" ", Enumerable.Repeat(Input, 100));
        static string NoWhitespace(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Equal(NoWhitespace(large), NoWhitespace(Format(large)));
    }

    [Fact]
    public void SplitterKeepsAbbreviationsAndOrdinalsTogether()
    {
        const string text = "Wir sehen uns am 3. Mai, z. B. im Büro. Danach geht es weiter! Oder? Ja";
        var sentences = SentenceSplitter.Split(text).Select(r => text[r.Start..r.End].Trim()).ToList();
        Assert.Equal(new[] { "Wir sehen uns am 3. Mai, z. B. im Büro.", "Danach geht es weiter!", "Oder?", "Ja" }, sentences);
    }
}
