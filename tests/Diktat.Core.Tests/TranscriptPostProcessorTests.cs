using System.Diagnostics;
using System.Globalization;
using Diktat.Core.Settings;
using Diktat.Core.Support;
using Diktat.Core.Text;

namespace Diktat.Core.Tests;

public class TranscriptPostProcessorTests
{
    private static readonly string[] Terms = ["WhiteRabbit", "HubSpot", "Claude", "Copper", "iPhone", "n8n", "Jamf", "Magnussen"];
    private static readonly HashSet<string> Dictionary = ["cloud", "blackbox", "jam"];
    private static readonly double Threshold = new TranscriptPostProcessor().CorrectionThreshold;

    private static TranscriptPostProcessor Processor(bool correct = true, bool removeFillers = true) => new()
    {
        CustomTerms = Terms,
        CorrectCustomTerms = correct,
        RemoveFillerWords = removeFillers,
        Language = "de",
        IsDictionaryWord = word => Dictionary.Contains(word.ToLowerInvariant()),
    };

    // Custom terms

    [Fact]
    public void MergesMultiWordArtifacts()
    {
        Assert.Equal("Die Kontakte liegen in HubSpot.", Processor().Process("Die Kontakte liegen in Hub Spot."));
        Assert.Equal("Das ist WhiteRabbit.", Processor().Process("Das ist White Rabbit."));
        Assert.Equal("Prüf n8n bitte.", Processor().Process("Prüf n 8 n bitte."));
    }

    [Fact]
    public void DoesNotMergeAcrossPunctuation()
    {
        Assert.Equal("Erst White, dann Rabbit.", Processor().Process("Erst White, dann Rabbit."));
        Assert.Equal("Das war White. Rabbit kommt später.", Processor().Process("Das war White. Rabbit kommt später."));
    }

    [Fact]
    public void FixesMisheardSingleWords()
    {
        Assert.Equal("Frag mal Claude.", Processor().Process("Frag mal Clawd."));
        Assert.Equal("Copper ist offen", Processor().Process("Kopper ist offen"));
        Assert.Equal("Magnussen kommt", Processor().Process("Magnusen kommt"));
    }

    [Fact]
    public void KeepsRealDictionaryWords()
    {
        Assert.Equal("Das liegt in der Cloud.", Processor().Process("Das liegt in der Cloud."));
        Assert.Equal("Das Whiteboard bleibt.", Processor().Process("Das Whiteboard bleibt."));
    }

    [Fact]
    public void UsesConfiguredSpelling()
    {
        Assert.Equal("Schick das ans iPhone.", Processor().Process("Schick das ans iphone."));
        Assert.Equal("WhiteRabbit rockt", Processor().Process("whiterabbit rockt"));
    }

    [Fact]
    public void CorrectionCanBeDisabled() =>
        Assert.Equal("Frag mal Clawd.", Processor(correct: false).Process("Frag mal Clawd."));

    [Fact]
    public void LeavesTextWithoutMatchesUntouched()
    {
        const string text = "Zeile eins\nZeile zwei";
        Assert.Equal(text, Processor().Process(text));
    }

    [Fact]
    public void MatchesSpokenDigitsInTerms()
    {
        Assert.Equal("Den Workflow baue ich in n8n.", Processor().Process("Den Workflow baue ich in n acht n."));
        Assert.Equal("Läuft das in n8n?", Processor().Process("Läuft das in N Acht N?"));
        Assert.Equal("Ich nehme n8n dafür.", Processor().Process("Ich nehme n eight n dafür."));
    }

    [Fact]
    public void KeepsShortAndCommonWords()
    {
        Assert.Equal("Ja, das passt.", Processor().Process("Ja, das passt."));
        Assert.Equal("Wir sehen uns am Montag.", Processor().Process("Wir sehen uns am Montag."));
        Assert.Equal("Mach bitte eine Kopie davon.", Processor().Process("Mach bitte eine Kopie davon."));
        Assert.Equal("Der Jam gestern war gut.", Processor().Process("Der Jam gestern war gut."));
        Assert.Equal("Die Koffer sind gepackt.", Processor().Process("Die Koffer sind gepackt."));
        Assert.Equal("Ist das Klo da hinten?", Processor().Process("Ist das Klo da hinten?"));
    }

    [Fact]
    public void KeepsOtherWordsBuiltOnATerm()
    {
        // A name of its own, and the term with a genitive ending.
        Assert.Equal("Ich treffe Magnus morgen.", Processor().Process("Ich treffe Magnus morgen."));
        Assert.Equal("Magnussens Vorschlag steht.", Processor().Process("Magnussens Vorschlag steht."));
        // The word in front is not swallowed into the term.
        Assert.Equal("Leg das in Hubspott an.", Processor().Process("Leg das in Hubspott an."));
    }

    [Fact]
    public void FuzzyMatchesSkipKnownWords()
    {
        var processor = Processor();
        processor.IsDictionaryWord = word => new[] { "whiterabbet", "hab", "spot" }.Contains(word.ToLowerInvariant());
        // Written as one, "White Rabbet" is a word; "Hab" and "Spot" are words each.
        Assert.Equal("Schreib es an das White Rabbet.", processor.Process("Schreib es an das White Rabbet."));
        Assert.Equal("Leg das in Hab Spot an.", processor.Process("Leg das in Hab Spot an."));

        processor.IsDictionaryWord = _ => false;
        Assert.Equal("Schreib es an das WhiteRabbit.", processor.Process("Schreib es an das White Rabbet."));
        Assert.Equal("Leg das in HubSpot an.", processor.Process("Leg das in Hab Spot an."));
    }

    [Fact]
    public void DoesNotJoinAcrossLineBreaks() =>
        Assert.Equal("Das ist Black\nBoat", Processor().Process("Das ist Black\nBoat"));

    [Fact]
    public void KeepsWhitespaceAroundCorrections()
    {
        Assert.Equal("Zeile eins\nFrag Claude", Processor().Process("Zeile eins\nFrag Clawd"));
        Assert.Equal(
            "Erste Zeile\n\n  Frag  Claude\tund  Copper.\n",
            TranscriptPostProcessor.ApplyCustomTerms("Erste Zeile\n\n  Frag  Clawd\tund  Kopper.\n", Terms, Threshold));
    }

    [Fact]
    public void KeepsPunctuationAroundCorrections() =>
        Assert.Equal("Frag (iPhone) und \"Claude\"!", Processor().Process("Frag (iphone) und \"Clawd\"!"));

    [Fact]
    public void EmailAddresses()
    {
        // A dictation that is only an address stays lowercase.
        Assert.Equal("test@outlook.com", new TranscriptPostProcessor().Process("Test at outlook.com"));
        // A similar custom term never changes a part of an address.
        var processor = new TranscriptPostProcessor { CustomTerms = ["Meyer", "Firmo"] };
        Assert.Equal("Schreib an meier@firma.de, danke Meyer.", processor.Process("Schreib an meier at firma punkt de, danke Meier."));
    }

    [Fact]
    public void HandlesUmlautsAndSharpS()
    {
        var processor = new TranscriptPostProcessor
        {
            CustomTerms = ["Müller", "Strauß"],
            IsDictionaryWord = word => word.ToLowerInvariant() == "straße",
        };
        Assert.Equal("Grüße an Frau Müller, bis bald.", processor.Process("Grüße an Frau Müler, bis bald."));
        Assert.Equal("Herr Müller kommt.", processor.Process("Herr MUELLER kommt."));
        Assert.Equal("Familie Strauß zahlt.", processor.Process("Familie Strauss zahlt."));
        // Close to the term, but a real word.
        Assert.Equal("Die Straße bleibt.", processor.Process("Die Straße bleibt."));
    }

    [Fact]
    public void HandlesDecomposedUmlauts()
    {
        // Text pasted from some sources arrives decomposed ("u" + combining diaeresis).
        var processor = new TranscriptPostProcessor { CustomTerms = ["Müller"] };
        Assert.Equal("Grüße an Frau Müller.", processor.Process("Grüße an Frau Müler."));
        Assert.Equal(1.0, TranscriptPostProcessor.TermSimilarity("Müller", "Müller"));
    }

    [Fact]
    public void PrefersLongerSpanOnEqualScores() =>
        Assert.Equal("Leg es in HubSpot ab.", TranscriptPostProcessor.ApplyCustomTerms("Leg es in hub spot ab.", ["Hub", "HubSpot"], Threshold));

    [Fact]
    public void PrefersClosestTerm() =>
        Assert.Equal("Frag Claude.", TranscriptPostProcessor.ApplyCustomTerms("Frag Clawd.", ["Claudia", "Claude"], Threshold));

    [Fact]
    public void DigitsAreNeverGuessed() =>
        Assert.Equal("Siehe Projekt7.", TranscriptPostProcessor.ApplyCustomTerms("Siehe Projekt7.", ["Projekt8"], Threshold));

    [Fact]
    public void LongTranscriptWithManyTermsIsFast()
    {
        const string sentence = "Ich habe mit Clawd gestern über den Export in Hub Spot gesprochen, und Kopper ist offen. "
            + "Wir treffen uns um zehn Uhr bei Magnusen im Büro, danach baue ich den Workflow "
            + "in n acht n und schicke dir die Zusammenfassung aufs iphone.";
        const string corrected = "Ich habe mit Claude gestern über den Export in HubSpot gesprochen, und Copper ist offen. "
            + "Wir treffen uns um zehn Uhr bei Magnussen im Büro, danach baue ich den Workflow "
            + "in n8n und schicke dir die Zusammenfassung aufs iPhone.";
        var sentenceWords = sentence.Split(' ');
        var text = string.Join(" ", Enumerable.Range(0, 2_000).Select(i => sentenceWords[i % sentenceWords.Length]));

        // 100 terms: the usual ones plus made-up names.
        string[] syllables = ["ka", "lo", "mir", "ten", "dra", "vos", "bel", "quin", "sto", "ra", "pex", "ul", "fen"];
        var count = syllables.Length;
        var madeUp = Enumerable.Range(0, 100 - Terms.Length).Select(index =>
        {
            var word = syllables[index % count] + syllables[(index / count + 5) % count] + syllables[(index * 7 + 3) % count];
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word);
        });
        var manyTerms = Terms.Concat(madeUp).ToList();
        Assert.Equal(100, manyTerms.Distinct().Count());
        // Like the app's dictionary: the regular words are known, the misheard ones are not.
        var misheard = new HashSet<string> { "clawd", "kopper", "magnusen" };
        var known = sentenceWords.Select(w => w.ToLowerInvariant().Trim(',', '.', '!', '?')).Where(w => !misheard.Contains(w)).ToHashSet();

        // Warm up once (JIT), then measure.
        TranscriptPostProcessor.ApplyCustomTerms(sentence, manyTerms, Threshold, w => known.Contains(w.ToLowerInvariant()));
        var stopwatch = Stopwatch.StartNew();
        var result = TranscriptPostProcessor.ApplyCustomTerms(text, manyTerms, Threshold, w => known.Contains(w.ToLowerInvariant()));
        stopwatch.Stop();
        Assert.StartsWith(corrected, result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"took {stopwatch.Elapsed.TotalMilliseconds} ms");
    }

    // Replacements

    [Fact]
    public void AppliesReplacements()
    {
        var processor = Processor();
        processor.Replacements = [new TermReplacement("en acht en", "n8n"), new TermReplacement("Nodemation", "n8n")];
        Assert.Equal("Das läuft in n8n.", processor.Process("Das läuft in en acht en."));
        Assert.Equal("Das läuft in n8n, oder?", processor.Process("Das läuft in En-Acht-En, oder?"));
        Assert.Equal("n8n ist offen", processor.Process("nodemation ist offen"));
    }

    [Fact]
    public void ReplacementsMatchWholeWordsOnly()
    {
        var processor = Processor();
        processor.Replacements = [new TermReplacement("kai", "KI")];
        Assert.Equal("KI fragt die KI.", processor.Process("Kai fragt die kai."));
        Assert.Equal("Kaiser bleibt Kaiser.", processor.Process("Kaiser bleibt Kaiser."));
    }

    [Fact]
    public void ReplacementsWorkWithCorrectionDisabled()
    {
        var processor = Processor(correct: false);
        processor.Replacements = [new TermReplacement("en acht en", "n8n")];
        Assert.Equal("in n8n", processor.Process("in en acht en"));
    }

    [Fact]
    public void ReplacementTextIsLiteral()
    {
        var processor = Processor();
        processor.Replacements = [new TermReplacement("dollar eins", "$1 und $0")];
        Assert.Equal("Das kostet $1 und $0.", processor.Process("Das kostet dollar eins."));
    }

    // Filler words

    [Fact]
    public void RemovesGermanFillers()
    {
        Assert.Equal("Ich schreibe dir wegen, dem Export.", Processor().Process("Äh, ich schreibe dir wegen, ähm, dem Export."));
        Assert.Equal("Also, um 10 Uhr passt.", Processor().Process("Also, äh, um 10 Uhr passt."));
        Assert.Equal("Das stimmt.", Processor().Process("Das stimmt hmm."));
        Assert.Equal("Okay.", Processor().Process("Ähm. Okay."));
    }

    [Fact]
    public void KeepsRealWordsThatLookLikeFillers()
    {
        // "um" is a German preposition and must survive.
        Assert.Equal("Wir treffen uns um 10 Uhr.", Processor().Process("Wir treffen uns um 10 Uhr."));
        Assert.Equal("Ähnlich wie ähnlich.", Processor().Process("Ähnlich wie ähnlich."));
    }

    [Fact]
    public void FillerRemovalCanBeDisabled() =>
        Assert.Equal("Äh, ja.", Processor(removeFillers: false).Process("Äh, ja."));

    [Fact]
    public void CollapsesStutters()
    {
        Assert.Equal("ich glaube das", Processor().Process("ich ich ich ich glaube das"));
        Assert.Equal("das das ist gut", Processor().Process("das das ist gut"));
    }

    [Fact]
    public void OnlyFillersResultsInEmptyText() => Assert.Equal("", Processor().Process("Ähm, äh."));

    [Fact]
    public void RemovesStretchedFillersButKeepsWords()
    {
        Assert.Equal("Das ist eh egal.", Processor().Process("Ähhhm, HMMM, das ist eh egal."));
        Assert.Equal("Mhm, passt.", Processor().Process("Mhm, passt."));
    }

    [Fact]
    public void FillerListsDependOnLanguage()
    {
        var processor = Processor();
        processor.Language = "en-US";
        Assert.Equal("We meet at ten, maybe.", processor.Process("Um, we meet at ten, uh, maybe."));
        processor.Language = "de_DE";
        Assert.Equal("Um zehn, passt.", processor.Process("Ähm, um zehn, äh, passt."));
        // Other languages only lose sounds that are no word anywhere.
        processor.Language = "pt";
        Assert.Equal("Uh, um café.", processor.Process("Hmm, uh, um café."));
    }

    [Fact]
    public void StuttersKeepFirstWordAndLastPunctuation()
    {
        Assert.Equal("Gut. Ich glaube schon.", Processor().Process("Gut. Ich, ich, ich glaube schon."));
        Assert.Equal("Das war es.", Processor().Process("Das war es es es."));
    }

    [Fact]
    public void StuttersKeepDeliberateRepetition()
    {
        Assert.Equal("Nein. Nein. Nein.", Processor().Process("Nein. Nein. Nein."));
        Assert.Equal("Es kostet 10 10 10 Euro.", Processor().Process("Es kostet 10 10 10 Euro."));
        Assert.Equal("das das ist gut", TranscriptPostProcessor.CollapseStutters("das das ist gut"));
    }

    // Repeated phrases

    [Fact]
    public void RepeatedPhrasesKeepFirstCopyAndLastPunctuation()
    {
        Assert.Equal("Wir müssen das klären.", Processor().Process("Wir müssen wir müssen das klären."));
        Assert.Equal("Ich gehe morgen ins Büro.", Processor().Process("Ich gehe morgen, ich gehe morgen ins Büro."));
    }

    [Fact]
    public void RepeatedPhrasesCollapseThreeCopies() =>
        Assert.Equal("wir müssen los", TranscriptPostProcessor.CollapseRepeatedPhrases("wir müssen wir müssen wir müssen los"));

    [Fact]
    public void RepeatedPhrasesKeepDeliberateRepetition()
    {
        Assert.Equal("Es geht los. Es geht los. Danach folgt mehr.", TranscriptPostProcessor.CollapseRepeatedPhrases("Es geht los. Es geht los. Danach folgt mehr."));
        Assert.Equal("Die Zahl ist 1 2 1 2.", TranscriptPostProcessor.CollapseRepeatedPhrases("Die Zahl ist 1 2 1 2."));
        Assert.Equal("das das ist gut", TranscriptPostProcessor.CollapseRepeatedPhrases("das das ist gut"));
    }

    [Fact]
    public void EmojisAndOtherSymbolsStay()
    {
        Assert.Equal("Super 👍🏽, bis morgen 🎉", Processor().Process("Super 👍🏽, äh, bis morgen 🎉"));
        Assert.Equal("Frag Claude 🙂", Processor().Process("Frag Clawd 🙂"));
    }

    // Building blocks

    [Fact]
    public void ColognePhoneticsReferenceExamples()
    {
        Assert.Equal("65752682", TranscriptPostProcessor.ColognePhonetics("Müller-Lüdenscheidt"));
        Assert.Equal("3412", TranscriptPostProcessor.ColognePhonetics("Wikipedia"));
        Assert.Equal("17863", TranscriptPostProcessor.ColognePhonetics("Breschnew"));
    }

    [Fact]
    public void ColognePhoneticsGroupsSimilarSounds()
    {
        static string Code(string word) => TranscriptPostProcessor.ColognePhonetics(word);
        Assert.Equal(Code("Copper"), Code("Kopper"));
        Assert.Equal(Code("Mayr"), Code("Meier"));
        Assert.Equal(Code("Filip"), Code("Philipp"));
        Assert.Equal(Code("Mueller"), Code("Müller"));
        // C is hard at the start before a, soft before e and after s; x is "ks".
        Assert.Equal("475", Code("Carl"));
        Assert.Equal("85", Code("Celle"));
        Assert.Equal("862", Code("Schmidt"));
        Assert.Equal("4837", Code("Xaver"));
    }

    [Fact]
    public void EditDistanceCountsNeighbourSwapsOnce()
    {
        Assert.Equal(3, TranscriptPostProcessor.EditDistance("kitten", "sitting"));
        Assert.Equal(1, TranscriptPostProcessor.EditDistance("ab", "ba"));
        Assert.Equal(2, TranscriptPostProcessor.EditDistance("Clawd", "Claude"));
        Assert.Equal(3, TranscriptPostProcessor.EditDistance("", "abc"));
        Assert.Equal(0, TranscriptPostProcessor.EditDistance("Müller", "Müller"));
        Assert.Equal(1, TranscriptPostProcessor.EditDistance("Müller", "Muller"));
    }

    [Fact]
    public void SpellingAndSoundSimilarity()
    {
        Assert.Equal(1, TranscriptPostProcessor.SpellingSimilarity("Müller", "MUELLER"));
        Assert.Equal(5.0 / 6, TranscriptPostProcessor.SpellingSimilarity("Kopper", "Copper"), 9);
        Assert.Equal(1, TranscriptPostProcessor.SoundSimilarity("Kopper", "Copper"));
        // "aw" before a consonant is a vowel, as in English.
        Assert.Equal(1, TranscriptPostProcessor.SoundSimilarity("Clawd", "Claude"));
        Assert.Equal(2.0 / 3, TranscriptPostProcessor.SoundSimilarity("glaube", "Claude"), 9);
    }

    [Fact]
    public void TermSimilarityScale()
    {
        static double Similarity(string a, string b) => TranscriptPostProcessor.TermSimilarity(a, b);
        Assert.Equal(1, Similarity("Hub Spot", "HubSpot"));
        Assert.Equal(1, Similarity("MUELLER", "Müller"));
        Assert.Equal(1, Similarity("n acht n", "n8n"));
        Assert.Equal(1, Similarity("N eight N", "n8n"));
        Assert.Equal(0, Similarity("Jams", "Jamf")); // too short to guess
        Assert.Equal(0, Similarity("Projekt7", "Projekt8")); // digits are never fuzzy
        Assert.Equal(0, Similarity("Magnus", "Magnussen")); // only the start of the term
        Assert.Equal(0, Similarity("Magnussens", "Magnussen")); // the term plus an ending
        Assert.Equal(0, Similarity("Hopper", "Copper")); // another first sound
        Assert.Equal(0, Similarity("in Hubspott", "HubSpot")); // another first letter
        Assert.True(Similarity("Claud", "Claude") > Threshold, "one missing letter is fine");
    }

    [Fact]
    public void ThresholdLeavesMarginOnBothSides()
    {
        (string Heard, string Term)[] misheard = [("Clawd", "Claude"), ("Kopper", "Copper"), ("Magnusen", "Magnussen"), ("Müler", "Müller"), ("Hab Spot", "HubSpot")];
        foreach (var (heard, term) in misheard)
        {
            Assert.True(TranscriptPostProcessor.TermSimilarity(heard, term) >= Threshold + 0.05, $"{heard} -> {term}");
        }
        (string Heard, string Term)[] otherWords = [("glaube", "Claude"), ("Klause", "Claude"), ("Klo da", "Claude"), ("Kopie", "Copper"), ("Koffer", "Copper")];
        foreach (var (heard, term) in otherWords)
        {
            Assert.True(TranscriptPostProcessor.TermSimilarity(heard, term) <= Threshold - 0.05, $"{heard} -> {term}");
        }
    }

    // Wiring

    [Fact]
    public void FromSettingsUsesTheDictionaryChecker()
    {
        var settings = new SettingsContainer();
        settings.TextImprovement.CustomTerms = ["Claude"];
        settings.Transcription.Language = "de";
        var checker = new FakeChecker("cloud");
        var processor = TranscriptPostProcessor.FromSettings(settings, checker);
        Assert.Equal("Frag Claude in der Cloud.", processor.Process("Frag Clawd in der Cloud."));
        Assert.Contains(("Cloud", "de"), checker.Asked);
    }

    private sealed class FakeChecker(params string[] words) : IDictionaryWordChecker
    {
        public List<(string, string)> Asked { get; } = [];

        public bool IsDictionaryWord(string word, string languageCode)
        {
            Asked.Add((word, languageCode));
            return words.Contains(word.ToLowerInvariant());
        }
    }
}
