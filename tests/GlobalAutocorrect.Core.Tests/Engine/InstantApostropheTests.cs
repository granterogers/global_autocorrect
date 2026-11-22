using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Tests.Instant;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D172 (stress, replayed through the shipped dictionary): a contraction typed without its apostrophe was
/// "fixed" to a different, correct-looking word: "wasnt" → "want", "weve" → "were", "im" → "in", "shes" → "she".</summary>
public sealed class InstantApostropheTests
{
    private static readonly Lazy<SymSpellSuggester> Suggester = new(() => SymSpellSuggester.FromAssets(HunspellTests.Assets()));

    [Theory]
    [InlineData("wasnt", "wasn't")]
    [InlineData("weve", "we've")]
    [InlineData("im", "I'm")]
    [InlineData("shes", "she's")]
    [InlineData("didnt", "didn't")]
    [InlineData("teh", "the")]        // not a contraction: unchanged
    [InlineData("tthis", "this")]
    [InlineData("llief", null)]
    public void R4_3_D172_AContractionWithoutItsApostropheIsThatContraction(string typed, string? expected)
    {
        var c = new InstantCorrector(HunspellTests.Shared, Suggester.Value, _ => false);
        Assert.Equal(expected, c.TryCorrect(typed, isSentenceStart: false));
    }

    [Fact]
    public void R4_3_D172_ASentenceStartKeepsItsCapital()
    {
        var c = new InstantCorrector(HunspellTests.Shared, Suggester.Value, _ => false);
        Assert.Equal("We've", c.TryCorrect("Weve", isSentenceStart: true));
    }
}
