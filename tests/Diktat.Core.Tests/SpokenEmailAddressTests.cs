using Diktat.Core.Text;

namespace Diktat.Core.Tests;

public class SpokenEmailAddressTests
{
    [Fact]
    public void SpokenAtBeforeWrittenDomain() =>
        Assert.Equal("Das ist ein test@outlook.com", SpokenEmailAddress.Format("Das ist ein Test at outlook.com"));

    [Fact]
    public void SpokenDots()
    {
        Assert.Equal("Schreib an max.meier@firma.de bitte", SpokenEmailAddress.Format("Schreib an max punkt meier at firma punkt de bitte"));
        Assert.Equal("info@example.com", SpokenEmailAddress.Format("info ät example dot com"));
    }

    [Fact]
    public void WrittenAtSignWithSpaces() =>
        Assert.Equal("an paul@example.com", SpokenEmailAddress.Format("an paul @ example.com"));

    [Fact]
    public void KeepsSentenceEndOutsideTheAddress() =>
        Assert.Equal("Danke. test@outlook.com.", SpokenEmailAddress.Format("Danke. test at outlook.com."));

    [Fact]
    public void RecognizerSpaceAfterTheDot() =>
        Assert.Equal("test@outlook.com", SpokenEmailAddress.Format("test at outlook. com"));

    [Theory]
    [InlineData("Wir treffen uns at home und dann geht's los.")]
    [InlineData("Look at this, it is great.")]
    [InlineData("Die Domain heißt outlook punkt com.")]
    public void LeavesOrdinaryAtAlone(string sentence) => Assert.Equal(sentence, SpokenEmailAddress.Format(sentence));

    [Theory]
    [InlineData("Look at this. It is great.")]
    [InlineData("Look at me. No way, I said.")]
    [InlineData("Er war at home. De facto blieb er da.")]
    public void SentenceEndIsNoDot(string sentence) => Assert.Equal(sentence, SpokenEmailAddress.Format(sentence));

    [Fact]
    public void CapitalTopLevelDomainAtTheVeryEndStillCounts() =>
        Assert.Equal("test@outlook.com.", SpokenEmailAddress.Format("Test at outlook. Com."));

    [Theory]
    [InlineData("Find us at example.com")]
    [InlineData("It is available at apple.com.")]
    [InlineData("More info at example.com")]
    [InlineData("You can reach me at paul at example dot com")]
    public void EnglishAtBeforeAWebsite(string sentence) => Assert.Equal(sentence, SpokenEmailAddress.Format(sentence));

    [Fact]
    public void WrittenAtIsAnAddressInEveryLanguage() =>
        Assert.Equal("Write to paul@example.com", SpokenEmailAddress.Format("Write to paul @ example.com"));

    [Fact]
    public void PunktAsNoun()
    {
        Assert.Equal("Wichtiger Punkt info@firma.de", SpokenEmailAddress.Format("Wichtiger Punkt info at firma punkt de"));
        Assert.Equal("Der letzte Punkt info@firma.de", SpokenEmailAddress.Format("Der letzte Punkt info at firma punkt de"));
        Assert.Equal("paul.stegmann@example.com", SpokenEmailAddress.Format("Paul Punkt Stegmann at Example Punkt com"));
    }

    [Fact]
    public void OnlyAnAddress()
    {
        Assert.Equal("test@outlook.com", SpokenEmailAddress.Format("Test at outlook.com"));
        Assert.Equal("paul@example.com", SpokenEmailAddress.Format("Paul at example.com"));
    }

    [Fact]
    public void TwoAddresses() =>
        Assert.Equal("a@b.de und c@d.com", SpokenEmailAddress.Format("a at b punkt de und c at d punkt com"));
}
