using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Learning;
using GlobalAutocorrect.Core.Tests.EvalData;
using GlobalAutocorrect.Core.Text;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Learning;

/// <summary>
/// D175 (user: "the code itself like something that's dynamic and improves without the actual binaries needing to change"):
/// the word lists and rule switches come from the brain, so a correction can be changed without a release.
/// </summary>
[Collection("Tunables")]
public sealed class WordListsTests
{
    private static readonly HunspellSpellChecker Speller = HunspellSpellChecker.FromAssets(Path.Combine(EvalDataFiles.RepoRoot(), "assets"));
    private static readonly InstantCorrector Instant = new(Speller, SymSpellSuggester.FromAssets(Path.Combine(EvalDataFiles.RepoRoot(), "assets")), _ => false);
    private static readonly EditRules Rules = new(new EditRuleSettings(), Speller, _ => false);

    private static string Apply(string a, string b) => TextEdit.ApplyAll(a, Rules.Filter(a, b, WordDiff.Compute(a, b)).Accepted);

    private static BrainFile Brain(string words, string tuning = "{}") =>
        GlobalAutocorrect.Core.Learning.Brain.Parse($$"""{ "format": 1, "updated": "2026-11-23T12:00:00Z", "tuning": {{tuning}}, "words": {{words}} }""")!;

    private static void Use(BrainFile b) { WordLists.Apply(b.Words); Tunables.Apply(b.Tuning); }

    private static void Reset() { WordLists.Apply(null); Tunables.Apply(null); }

    [Fact]
    public void R4_3_D175_TheBrainsOwnFixesAndLeftWordsReachTheInstantLayer()
    {
        try
        {
            Assert.Null(Instant.TryCorrect("zorbix", false));
            Use(Brain("""{ "Instant.Fixes": { "add": ["zorbix=zebra"] }, "Instant.Leave": { "add": ["teh"] } }"""));
            Assert.Equal("zebra", Instant.TryCorrect("zorbix", false));
            Assert.Equal("Zebra", Instant.TryCorrect("Zorbix", true));   // the typed capital kept
            Assert.Null(Instant.TryCorrect("teh", false));                // left to the AI
            Reset();
            Assert.Null(Instant.TryCorrect("zorbix", false));             // the brain dropped it: gone
            Assert.Equal("the", Instant.TryCorrect("teh", false));
        }
        finally { Reset(); }
    }

    [Fact]
    public void R4_5_D175_ANeverChangeWordAndAKnownWordAreLeftByEveryLayer()
    {
        try
        {
            Use(Brain("""{ "Words.NeverChange": { "add": ["Grnatly"] }, "Words.Known": { "add": ["blorptastic"] } }"""));
            Assert.Null(Instant.TryCorrect("Grnatly", true));
            Assert.Equal("meet Grnatly at the station", Apply("meet Grnatly at teh station", "meet Grantly at the station"));
            Assert.True(Speller.IsValid("blorptastic"));
            Use(Brain("""{ "Words.Known": { "remove": ["asap"] } }"""));
            Assert.False(EverydayWords.Contains("asap"));                 // a built-in entry taken out
        }
        finally { Reset(); }
    }

    [Fact]
    public void R4_5_D175_ARuleCanBeSwitchedOffFromTheBrain()
    {
        const string typed = "If you can kindly give me an alternate, that would be apprectea d.";
        const string meant = "If you can kindly give me an alternate, that would be appreciated.";
        try
        {
            Assert.Equal(meant, Apply(typed, meant));
            Use(Brain("{}", """{ "Rules.JoinNonWords": 0 }"""));
            Assert.Equal(typed, Apply(typed, meant));                     // D173's join off again, no release
            Use(Brain("{}", """{ "Rules.WholeRejectOnProtected": 1 }"""));
            Assert.Equal("meet teh team at 3pm", Apply("meet teh team at 3pm", "meet the team at 4pm"));   // 4.5.d as written
        }
        finally { Reset(); }
    }

    [Fact]
    public void R4_5_D175_ABrainEntryThatDoesntFitIsLeftOut()
    {
        var b = Brain("""
            { "Instant.Fixes": { "add": ["nopair"] }, "No.Such.List": { "add": ["x"] }, "Words.Known": { "add": ["ok\u0007"] },
              "Words.Function": { "add": ["thou"] } }
            """);
        Assert.Equal(["Words.Function"], b.Words.Keys.ToArray());          // a pair without "=", an unknown list, a control character
    }

    [Fact]
    public void R4_5_D175_EveryListIsListedForWhoeverEditsTheBrain()
    {
        string readme = File.ReadAllText(Path.Combine(EvalDataFiles.RepoRoot(), "brain", "README.md"));
        Assert.NotEmpty(WordLists.All);
        foreach (var l in WordLists.All)
        {
            Assert.Contains($"`{l.Name}`", readme);
            Assert.False(string.IsNullOrWhiteSpace(l.Why), l.Name);
            Assert.All(l.Defaults, e => Assert.True(WordLists.EntryOk(e), $"{l.Name}: {e}"));
        }
    }
}
