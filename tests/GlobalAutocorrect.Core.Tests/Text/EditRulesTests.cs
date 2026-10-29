using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Text;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Text;

public sealed class EditRulesTests
{
    private sealed class FakeSpeller(params string[] words) : ISpellChecker
    {
        private readonly HashSet<string> _w = new(words, StringComparer.OrdinalIgnoreCase);
        public bool IsValid(string word) => _w.Contains(word);
    }

    private static readonly FakeSpeller Speller = new(
        "the", "cat", "sat", "on", "mat", "I", "am", "going", "home", "colour", "is", "nice", "happy", "glad",
        "feel", "today", "see", "you", "at", "call", "me", "meet", "on", "and", "a", "it", "was", "from", "form",
        "London", "organise", "event", "we", "will", "can", "next", "week", "report", "send");

    private static RuleResult Run(string a, string b, EditRuleSettings? s = null, params string[] personal)
    {
        var set = new HashSet<string>(personal, StringComparer.OrdinalIgnoreCase);
        return new EditRules(s ?? new EditRuleSettings(), Speller, set.Contains).Filter(a, b, WordDiff.Compute(a, b));
    }

    private static string Apply(string a, RuleResult r) => TextEdit.ApplyAll(a, r.Accepted);

    [Fact]
    public void R4_5_a_AcceptsCharacterSimilarWord()
    {
        Assert.Equal("the cat sat", Apply("teh cat sat", Run("teh cat sat", "the cat sat")));
    }

    [Fact]
    public void R4_5_a_AcceptsSpacingPunctuationApostropheCapitalisation()
    {
        const string a = "im in london.see you";
        var r = Run(a, "I'm in London. See you");
        Assert.Equal("I'm in London. See you", Apply(a, r));
    }

    [Fact]
    public void R4_4_d_RejectsSynonym()
    {
        var r = Run("I feel happy today", "I feel glad today");
        Assert.Empty(r.Accepted);
        Assert.Contains(r.Verdicts, v => v.Reason == RejectReason.TooDissimilar);
    }

    [Fact]
    public void R4_5_b_AcceptsFunctionWordInsertionButNotContentWords()
    {
        Assert.Equal("I am going home", Apply("I going home", Run("I going home", "I am going home")));
        Assert.Empty(Run("I going home", "I going home now").Accepted);
    }

    [Fact]
    public void R4_5_c_RejectsOneEditButKeepsTheRest()
    {
        const string a = "teh cat is happy and teh mat";
        var r = Run(a, "the cat is glad and the mat");
        Assert.Equal("the cat is happy and the mat", Apply(a, r));
    }

    [Fact]
    public void R4_5_d_RejectsWholeCorrectionWhenNumberChanges()
    {
        var r = Run("meet teh team at 3pm", "meet the team at 4pm");
        Assert.Equal(RejectReason.ProtectedTokenChanged, r.WholeRejection);
        Assert.Empty(r.Accepted);
    }

    [Fact]
    public void R4_5_d_RejectsWholeCorrectionWhenUrlOrEmailChanges()
    {
        Assert.Equal(RejectReason.ProtectedTokenChanged,
            Run("see www.exmaple.com teh end", "see www.example.com the end").WholeRejection);
        Assert.Equal(RejectReason.ProtectedTokenChanged,
            Run("mail jo@exmaple.com", "mail jo@example.com").WholeRejection);
    }

    [Fact]
    public void R4_5_d_RejectsWholeCorrectionWhenMoreThanHalfTheWordsChange()
    {
        var r = Run("teh cta sta on mat", "the cat sat on mat");   // 3 of 5 words
        Assert.Equal(RejectReason.TooManyChanges, r.WholeRejection);
    }

    [Fact]
    public void R4_5_e_SettingsAreConfigurable()
    {
        var strict = new EditRuleSettings { MaxNormalisedDistance = 0.2, MaxChangedWordRatio = 0.9 };
        Assert.Empty(Run("teh cat", "the cat", strict).Accepted);           // 0.33 > 0.2
        Assert.Single(Run("teh cat", "the cat").Accepted);
        var tight = new EditRuleSettings { MaxChangedWordRatio = 0.1 };
        Assert.Equal(RejectReason.TooManyChanges, Run("teh cat sat on the mat", "the cat sat on the mat", tight).WholeRejection);
    }

    [Fact]
    public void R3_4_NeverAmericanises()
    {
        var r = Run("the colour is nice", "the color is nice");
        Assert.Empty(r.Accepted);
        Assert.Equal(RejectReason.ReplacesValidWithInvalid, Assert.Single(r.Verdicts).Reason);
        Assert.Empty(Run("we will organise", "we will organize").Accepted);
    }

    [Fact]
    public void R4_5_a_AllowsRealWordErrorFixes()
    {
        Assert.Equal("it was from me", Apply("it was form me", Run("it was form me", "it was from me")));
    }

    [Fact]
    public void R8_5_NeverChangesPersonalDictionaryWords()
    {
        Assert.Empty(Run("call Grnat", "call Grant", null, "Grnat").Accepted);
    }

    [Fact]
    public void R4_3_c_NeverTouchesCodeLikeTokens()
    {
        Assert.Empty(Run("call getTxet now", "call getText now").Accepted);
    }
}
