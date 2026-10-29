using GlobalAutocorrect.Core.Instant;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Instant;

public sealed class HunspellTests
{
    internal static string Assets()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "GlobalAutocorrect.slnx"))) d = d.Parent;
        return Path.Combine(d!.FullName, "assets");
    }

    private static readonly Lazy<HunspellSpellChecker> Speller = new(() => HunspellSpellChecker.FromAssets(Assets()));
    internal static HunspellSpellChecker Shared => Speller.Value;

    [Theory]
    [InlineData("colour")]
    [InlineData("organise")]
    [InlineData("travelling")]
    [InlineData("realise")]
    [InlineData("don't")]
    [InlineData("don’t")]
    [InlineData("London")]
    [InlineData("the")]
    public void R3_4_BritishSpellingsAreValid(string w) => Assert.True(Speller.Value.IsValid(w));

    [Theory]
    [InlineData("color")]
    [InlineData("organize")]
    [InlineData("traveling")]
    [InlineData("teh")]
    [InlineData("bookk")]
    public void R3_4_AmericanSpellingsAndTyposAreInvalid(string w) => Assert.False(Speller.Value.IsValid(w));

    [Fact]
    public void R1_5_HunspellLoadsEnGbIse() => Assert.True(Speller.Value.IsValid("cancelled"));
}
