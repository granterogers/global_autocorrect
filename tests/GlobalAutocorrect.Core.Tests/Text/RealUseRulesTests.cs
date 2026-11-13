using GlobalAutocorrect.Core.Text;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Text;

/// <summary>D133: the stupid mistakes the first real-use run found (the real model on real-life messages).</summary>
public sealed class RealUseRulesTests
{
    [Fact]
    public void R4_5_D133_ShortFormsAndUnitsAreWords()
    {
        Assert.True(EverydayWords.Contains("mins"));
        Assert.True(EverydayWords.Contains("asap"));
        Assert.False(EverydayWords.Contains("mind"));
    }

    [Theory]
    [InlineData("adaptor", "adapter")]
    [InlineData("colour", "color")]
    [InlineData("organise", "organize")]
    [InlineData("centre", "center")]
    [InlineData("licence", "license")]
    [InlineData("travelled", "traveled")]
    [InlineData("programme", "program")]
    public void R4_5_D133_UkUsSpellingsAreVariants(string uk, string us)
    {
        Assert.True(EverydayWords.SpellingVariant(uk, us));
        Assert.True(EverydayWords.SpellingVariant(us, uk));
    }

    [Theory]
    [InlineData("for", "four")]
    [InlineData("form", "from")]
    [InlineData("their", "there")]
    [InlineData("recieve", "receive")]
    [InlineData("givaen", "given")]
    public void R4_5_D133_RealFixesAreNotVariants(string a, string b) => Assert.False(EverydayWords.SpellingVariant(a, b));

    [Fact]
    public void R4_5_D133_UntillIsATypoButTravelledIsASpelling()
    {
        Assert.False(EverydayWords.SpellingVariant("untill", "until", aIsAWord: false));
        Assert.True(EverydayWords.SpellingVariant("travelled", "traveled", aIsAWord: true));
        Assert.True(EverydayWords.SpellingVariant("adaptor", "adapter", aIsAWord: false));
    }
}
