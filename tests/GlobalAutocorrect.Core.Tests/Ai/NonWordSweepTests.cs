using GlobalAutocorrect.Core.Ai;
using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Prediction;
using GlobalAutocorrect.Core.Tests.Instant;
using GlobalAutocorrect.Core.Text;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Ai;

/// <summary>D38 with the real dictionary, UK word list and n-gram counts: what a misspelling left by the model becomes.</summary>
public sealed class NonWordSweepTests
{
    private static readonly Lazy<NonWordSweep> Sweep = new(() =>
    {
        string assets = HunspellTests.Assets();
        var ngram = NgramModel.FromAssets(assets);
        return new NonWordSweep(HunspellSpellChecker.FromAssets(assets), SymSpellSuggester.FromAssets(assets), w => w == "innergyish", ngram.PairCount);
    });

    /// <summary>As if the model had tried to change every word but the rules refused it all.</summary>
    private static TextEdit[] ModelTriedAll(string text) => [new TextEdit(0, text.Length, "", EditKind.Substitution, [], [], 0, text.Length)];

    private static string Apply(string text)
    {
        var fixes = Sweep.Value.Find(text, [], ModelTriedAll(text));
        foreach (var f in fixes.OrderByDescending(f => f.Start)) text = text[..f.Start] + f.Replacement + text[f.End..];
        return text;
    }

    [Theory]
    [InlineData("that would've hve been easier ", "that would've have been easier ")]   // user report: "hve" left in place
    [InlineData("easier thatn just the bus ", "easier than just the bus ")]
    [InlineData("I was going to the tstore today ", "I was going to the store today ")]
    [InlineData("we went to the shop the other dy and ", "we went to the shop the other day and ")]
    public void R4_4_a_AnObviousMisspellingLeftByTheModelIsFixed_D38(string text, string expected) =>
        Assert.Equal(expected, Apply(text));

    [Theory]
    [InlineData("the inorganic samples reacted ")]   // a real word, however rare
    [InlineData("I'm gonna be late ")]                // in the UK word list: slang, not a slip
    [InlineData("we met Zetters at the station ")]    // capitalised: a name
    [InlineData("the innergyish plan works ")]        // personal dictionary
    [InlineData("see you at the statoin")]            // the word still being typed (no space after it yet)
    [InlineData("call 07700 900123 or mail a@b.uk ")] // numbers and emails
    public void R4_4_d_TheSweepLeavesRealWordsNamesAndUnfinishedWordsAlone_D38(string text) =>
        Assert.Equal(text, Apply(text));

    [Theory]
    [InlineData("we stayed there for ne arly two hours ", "we stayed there for nearly two hours ")]   // both pieces non-words
    [InlineData("we ended up staying the ir for an hour ", "we ended up staying their for an hour ")]  // a word and a piece
    [InlineData("bread a nd milk please ", "bread and milk please ")]
    [InlineData("the runn ing club meets later ", "the running club meets later ")]
    public void R4_4_a_AWordSplitIntoPiecesIsJoined_D50(string text, string expected) =>
        Assert.Equal(expected, Apply(text));

    [Theory]
    [InlineData("we went in to the shop ")]          // two real words: never merged
    [InlineData("we met ne Arly at noon ")]           // a capitalised piece may be a name
    [InlineData("the xq zv thing broke ")]            // joined, still not a word
    [InlineData("see you at ne arly")]                // the word still being typed
    public void R4_4_d_PiecesAreJoinedOnlyWhenTheJoinIsAWord_D50(string text) =>
        Assert.Equal(text, Apply(text));

    [Fact]
    public void R4_4_a_HalfOfAWordTheModelRespacedIsNotTouched_D38()
    {
        // Found on the tuning set: "remem ber" → "remember" is an edit of the space alone; the sweep then turned "ber" into
        // "her" ("rememher"). The model's edit covers both halves.
        const string text = "please remem ber to log ";
        var spacing = new TextEdit(12, 13, "", EditKind.Spacing, ["remem", "ber"], ["remember"], 7, 16);
        Assert.Empty(Sweep.Value.Find(text, [spacing], ModelTriedAll(text)));
    }

    [Fact]
    public void R4_4_d_AWordTheModelLeftAloneIsNeverSwept_D38()
    {
        // "helpdesk" is a real word in neither dictionary: the model kept it, so the sweep must too.
        Assert.Empty(Sweep.Value.Find("report it to the helpdesk today ", [], []));
    }

    [Fact]
    public void R4_4_d_AddingOnlyAHyphenIsNotATypoFix_D38()
    {
        const string text = "report it to the helpdesk today ";
        Assert.Equal(text, Apply(text));
    }

    [Fact]
    public void R4_4_a_AWordTheModelAlreadyFixedIsNotTouchedAgain_D38()
    {
        const string text = "it was thatn the bus ";
        var accepted = new TextEdit(7, 12, "than", EditKind.Substitution, ["thatn"], ["than"], 7, 12);
        Assert.Empty(Sweep.Value.Find(text, [accepted], ModelTriedAll(text)));
    }
}
