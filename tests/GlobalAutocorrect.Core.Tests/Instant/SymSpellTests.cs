using System.Diagnostics;
using GlobalAutocorrect.Core.Instant;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Instant;

public sealed class SymSpellTests
{
    private static readonly Lazy<SymSpellSuggester> Suggester = new(() => SymSpellSuggester.FromAssets(HunspellTests.Assets()));

    private static InstantCorrector Corrector() => new(HunspellTests.Shared, Suggester.Value, _ => false);

    [Fact]
    public void R1_5_FreqDictLoads()
    {
        var lines = File.ReadAllLines(Path.Combine(HunspellTests.Assets(), "freq_en_GB.txt"));
        Assert.True(lines.Length >= 50_000);
        var words = lines.Select(l => l.Split(' ')[0]).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("the", words);
        Assert.Contains("colour", words);
        Assert.DoesNotContain("color", words);
        Assert.NotEmpty(Suggester.Value.Lookup("the"));
    }

    [Fact]
    public void R1_5_SuggestsTheForTeh()
    {
        var top = Suggester.Value.Lookup("teh")[0];
        Assert.Equal("the", top.Word);
        Assert.Equal(1, top.Distance);
    }

    [Fact]
    public void R0_2_a_RealDictionariesFixTehAndBookk()
    {
        var c = Corrector();
        Assert.Equal("the", c.TryCorrect("teh", false));
        Assert.Equal("book", c.TryCorrect("bookk", false));
        Assert.Equal("receive", c.TryCorrect("recieve", false));
    }

    [Theory]
    [InlineData("whenI'm")]   // user-style set: became "when's", and the model's "when I'm" was then refused
    [InlineData("andI'll")]
    [InlineData("soI've")]
    public void R4_3_c_D68_AMissedSpaceBeforeIContractionIsLeftToTheAi(string word) =>
        Assert.Null(Corrector().TryCorrect(word, false));

    [Fact]
    public void R3_4_NoAmericanSuggestions()
    {
        Assert.DoesNotContain(Suggester.Value.Lookup("colr", 10), s => s.Word == "color");
        // "colr" is also one edit from other real words (cold, cool, ...), so only "never color" is guaranteed for it.
        Assert.NotEqual("color", Corrector().TryCorrect("colr", false));
        Assert.Equal("colour", Corrector().TryCorrect("colur", false));
    }

    [Fact]
    public void R4_3_c_ValidAndCapitalisedWordsUntouched()
    {
        var c = Corrector();
        Assert.Null(c.TryCorrect("colour", false));
        Assert.Null(c.TryCorrect("Smiht", isSentenceStart: false));
    }

    [Fact]
    public void R4_3_a_LookupIsFast()
    {
        var s = Suggester.Value;
        string[] words = ["teh", "bookk", "recieve", "frist", "wrold", "thnk", "adn", "becuase", "wihch", "tiem"];
        s.Lookup("warmup");
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) s.Lookup(words[i % words.Length]);
        sw.Stop();
        Assert.True(sw.Elapsed.TotalMilliseconds / 1000 < 1.0, $"average {sw.Elapsed.TotalMilliseconds / 1000:F3} ms per lookup");
    }
}
