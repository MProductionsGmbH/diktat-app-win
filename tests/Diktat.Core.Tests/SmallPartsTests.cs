using Diktat.Core.Audio;
using Diktat.Core.Settings;
using Diktat.Core.Text;

namespace Diktat.Core.Tests;

public class AudioPaddingTests
{
    [Fact]
    public void AddsSilenceOnBothEnds()
    {
        var original = Enumerable.Repeat(0.5f, 2 * 16_000).ToArray();
        var padded = AudioPadding.PaddedCopy(original, 16_000)!;
        Assert.Equal((int)(16_000 * (2 + 2 * AudioPadding.Seconds)), padded.Length);
        Assert.Equal(0, padded[0]);
        Assert.Equal(0, padded[4_799]);
        Assert.Equal(0.5f, padded[4_800], 3);
        Assert.Equal(0, padded[^1]);
    }

    [Fact]
    public void LeavesLongRecordingsAlone()
    {
        var original = new float[(int)(8_000 * (AudioPadding.MaximumDuration.TotalSeconds + 1))];
        Assert.Null(AudioPadding.PaddedCopy(original, 8_000));
    }

    [Fact]
    public void InvalidSampleRateGivesNull() => Assert.Null(AudioPadding.PaddedCopy(new float[10], 0));
}

public class AdaptiveLevelTests
{
    [Fact]
    public void AQuietMicrophoneStillUsesTheFullScale()
    {
        var level = new AdaptiveLevel();
        // A webcam: speech peaks around 0.39 and mostly sits near 0.1 to 0.25.
        float loudest = 0;
        for (var raw = 0.1f; raw <= 0.39f + 1e-6f; raw += 0.03f) loudest = Math.Max(loudest, level.Normalize(raw));
        Assert.Equal(1, level.Normalize(0.39f), 3);
        Assert.Equal(1, loudest, 3);
        Assert.True(level.Normalize(0.25f) > 0.4f, "ordinary speech is well above the dots");
    }

    [Fact]
    public void RoomNoiseStaysAtZero()
    {
        var level = new AdaptiveLevel();
        foreach (var raw in new[] { 0f, 0.01f, 0.03f, 0.05f, 0.04f }) Assert.Equal(0, level.Normalize(raw));
    }

    [Fact]
    public void ALoudMicrophoneDoesNotClip()
    {
        var level = new AdaptiveLevel();
        Assert.Equal(1, level.Normalize(0.9f), 3);
        Assert.True(level.Normalize(0.45f) < 0.6f);
    }

    [Fact]
    public void TheScaleFallsBackAfterALoudMoment()
    {
        var level = new AdaptiveLevel();
        level.Normalize(0.9f);
        var right = level.Normalize(0.3f);
        for (var i = 0; i < 150; i++) level.Normalize(0.1f); // about seven and a half seconds
        Assert.True(level.Normalize(0.3f) > right + 0.2f);
        Assert.True(level.Peak >= AdaptiveLevel.MinimumPeak);
    }

    [Fact]
    public void ResetStartsFromTheFloorScale()
    {
        var level = new AdaptiveLevel();
        level.Normalize(0.9f);
        level.Reset();
        Assert.Equal(AdaptiveLevel.MinimumPeak, level.Peak, 4);
    }
}

public class PillPositionTests
{
    private const double Panel = 380;

    [Fact]
    public void TheMiddleOfThePillSitsAtTheChosenShareOfTheWidth()
    {
        Assert.Equal(2000, PillPosition.Right.OriginX(0, 3000, Panel) + Panel / 2, 0);
        Assert.Equal(1500, PillPosition.Center.OriginX(0, 3000, Panel) + Panel / 2, 0);
        Assert.Equal(1000, PillPosition.Left.OriginX(0, 3000, Panel) + Panel / 2, 0);
    }

    [Fact]
    public void AScreenNotAtTheOriginCountsFromItsOwnLeftEdge() =>
        Assert.Equal(1440 + 2000, PillPosition.Right.OriginX(1440, 3000, Panel) + Panel / 2, 0);

    [Fact]
    public void ThePanelStaysInsideAVeryNarrowArea()
    {
        foreach (var position in PillPositionInfo.AllCases)
        {
            var x = position.OriginX(0, 400, Panel);
            Assert.True(x >= 0);
            Assert.True(x + Panel <= 400);
        }
        // Wider than the area: left edge.
        Assert.Equal(10, PillPosition.Right.OriginX(10, 300, Panel));
    }

    [Fact]
    public void RightIsTheOnlyPositionRightOfTheMiddle()
    {
        Assert.True(PillPosition.Right.Share() > PillPosition.Center.Share());
        Assert.True(PillPosition.Left.Share() < PillPosition.Center.Share());
        Assert.Equal("Rechts", PillPosition.Right.DisplayName());
    }
}

public class TextPartsTests
{
    private const string Text = "Ich: Wir fangen mit den Zahlen an.\nAndere: Die liegen seit gestern vor.\nIch: Gut, dann weiter.";

    [Fact]
    public void ShortTextFitsWhole()
    {
        Assert.Equal(Text.Length, TextParts.EndOfLeadingPart(Text, 1_000));
        Assert.Equal(0, TextParts.StartOfTrailingPart(Text, 1_000));
    }

    [Fact]
    public void LeadingPartEndsAfterAWholeLine() =>
        Assert.Equal("Ich: Wir fangen mit den Zahlen an.\n", Text[..TextParts.EndOfLeadingPart(Text, 50)]);

    [Fact]
    public void LeadingPartOfOneLongLineEndsAfterAWord()
    {
        const string line = "Andere: Die liegen seit gestern vor und sind vollständig.";
        Assert.Equal("Andere: Die liegen ", line[..TextParts.EndOfLeadingPart(line, 20)]);
    }

    [Fact]
    public void TrailingPartStartsAtAWholeLine() =>
        Assert.Equal("Ich: Gut, dann weiter.", Text[TextParts.StartOfTrailingPart(Text, 40)..]);

    [Fact]
    public void PartsCoverEverything()
    {
        var rest = Text;
        var parts = new List<string>();
        while (rest.Length > 0)
        {
            var end = TextParts.EndOfLeadingPart(rest, 30);
            parts.Add(rest[..end]);
            rest = rest[end..];
        }
        Assert.Equal(Text, string.Concat(parts));
        Assert.All(parts, part => Assert.True(part.Length <= 30));
    }

    [Fact]
    public void EmojisAreNeverCutInHalf()
    {
        var text = string.Concat(Enumerable.Repeat("🙂", 10));
        var end = TextParts.EndOfLeadingPart(text, 3);
        Assert.Equal("🙂🙂🙂", text[..end]);
    }
}

public class ListDisplayTests
{
    [Fact]
    public void BulletPointsGetABullet()
    {
        const string summary = "Entscheidungen\n- Der Workshop findet online statt\n\nTo-dos\n- Paul: Angebot schicken";
        Assert.Equal("Entscheidungen\n• Der Workshop findet online statt\n\nTo-dos\n• Paul: Angebot schicken", ListDisplay.Bullets(summary));
    }

    [Fact]
    public void OtherDashesStay() =>
        Assert.Equal("Zahlen -5 % zum Vorjahr\n-Keine", ListDisplay.Bullets("Zahlen -5 % zum Vorjahr\n-Keine"));
}
