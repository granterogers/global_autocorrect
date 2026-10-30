using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Text;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Text;

public sealed class EditRulesTests
{
    /// <summary>Case rules like Hunspell: a lower-case entry is valid in any case; a capitalised entry (a name) only as written.</summary>
    private sealed class FakeSpeller(params string[] words) : ISpellChecker
    {
        private readonly HashSet<string> _w = new(words, StringComparer.Ordinal);
        public bool IsValid(string word) =>
            _w.Contains(word) || _w.Contains(word.ToLowerInvariant()) || (word.Length > 0 && _w.Contains(char.ToLowerInvariant(word[0]) + word[1..]));
    }

    private static readonly FakeSpeller Speller = new(
        "the", "cat", "sat", "on", "mat", "I", "am", "going", "home", "colour", "is", "nice", "happy", "glad",
        "feel", "today", "see", "you", "at", "call", "me", "meet", "and", "a", "it", "was", "from", "form",
        "London", "organise", "event", "we", "will", "can", "next", "week", "report", "send", "apply", "for",
        "senior", "analyst", "job", "in", "book", "taxi", "to", "station", "policy", "intranet");

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

    [Theory]
    [InlineData("it was nice by I am going", "it was nice but I am going")]   // missed key + neighbouring key
    [InlineData("we will to a taxi", "we will got a taxi")]                 // missed key + swap
    [InlineData("we will book a we taxi", "we will book a new taxi")]       // missed key + swap
    public void R4_5_a_TwoLikelyKeyboardSlipsAreATypo_D30(string typed, string model)
    {
        // Found on the tuning set: the model's fix was right and "too dissimilar" threw it away (short words).
        var s = new FakeSpeller("it", "was", "nice", "by", "but", "I", "am", "going", "we", "will", "to", "got", "a", "taxi", "book", "new");
        var r = new EditRules(new EditRuleSettings(), s, _ => false).Filter(typed, model, WordDiff.Compute(typed, model));
        Assert.Equal(model, Apply(typed, r));
    }

    [Fact]
    public void R4_4_d_TwoUnlikelyEditsAreStillRejected_D30()
    {
        // "it" → "on": i/o are neighbours but t/n are not, so it isn't two likely slips; it is a different word.
        var r = Run("I sat it the mat", "I sat on the mat");
        Assert.Empty(r.Accepted);
        Assert.Contains(r.Verdicts, v => v.Reason == RejectReason.TooDissimilar);
    }

    [Fact]
    public void R4_5_e_CautiousTurnsOffTwoSlips_D30()
    {
        var s = new FakeSpeller("it", "was", "nice", "by", "but", "I", "am", "going");
        const string a = "it was nice by I am going", b = "it was nice but I am going";
        var r = new EditRules(new EditRuleSettings { AcceptLikelyKeyboardSlips = false }, s, _ => false).Filter(a, b, WordDiff.Compute(a, b));
        Assert.Empty(r.Accepted);
    }

    [Fact]
    public void R4_5_a_ALoneLowerCaseIIsCapitalisedMidSentence_D25()
    {
        // Found on the tuning set: the dictionary lists "i" (the letter), so "i" → "I" was taken for a style change.
        var s = new FakeSpeller("i", "I", "and", "still", "think", "so", "i'm", "fine");
        foreach (var (a, b) in new[] { ("and i still think so", "and I still think so"), ("and i'm fine", "and I'm fine") })
        {
            var r = new EditRules(new EditRuleSettings(), s, _ => false).Filter(a, b, WordDiff.Compute(a, b));
            Assert.Equal(b, Apply(a, r));
        }
    }

    private static string ApplyWith(FakeSpeller s, string a, string b) =>
        Apply(a, new EditRules(new EditRuleSettings(), s, _ => false).Filter(a, b, WordDiff.Compute(a, b)));

    [Fact]
    public void R4_5_b_AFixNextToADroppedFunctionWordIsAccepted_D31()
    {
        // Found on the tuning set: "si" → "is the" was aligned as one edit and judged too dissimilar.
        var s = new FakeSpeller("because", "jumper", "si", "is", "the", "wrong", "size");
        Assert.Equal("because jumper is the wrong size", ApplyWith(s, "because jumper si wrong size", "because jumper is the wrong size"));
        // The extra word must be a function word: "si" → "is very" is still content added.
        var s2 = new FakeSpeller("because", "jumper", "si", "is", "very", "wrong", "size");
        Assert.Equal("because jumper si wrong size", ApplyWith(s2, "because jumper si wrong size", "because jumper is very wrong size"));
        // A note-style "Spare room needs…" is the writer's choice: an article plus a lost capital is not a typo fix.
        var s3 = new FakeSpeller("the", "spare", "room", "needs", "hoovering");
        Assert.Equal("Spare room needs hoovering", ApplyWith(s3, "Spare room needs hoovering", "The spare room needs hoovering"));
    }

    [Fact]
    public void R4_4_d_ACapitalAfterASentenceEndTheReplyAddsIsASentenceStart_D31()
    {
        // Found on the tuning set: "afternoon something" → "afternoon? Something" lost the capital.
        var s = new FakeSpeller("could", "we", "move", "it", "to", "afternoon", "something", "came", "up");
        Assert.Equal("could we move it to afternoon? Something came up",
            ApplyWith(s, "could we move it to afternoon something came up", "could we move it to afternoon? Something came up"));
        // Without the added full stop, the same capital mid-sentence is still a style change.
        Assert.Equal("could we move it to afternoon something came up",
            ApplyWith(s, "could we move it to afternoon something came up", "could we move it to afternoon Something came up"));
    }

    [Fact]
    public void R4_5_a_APossessiveOfAKnownWordIsAWord_D31()
    {
        var s = new FakeSpeller("the", "heating", "broken", "again");
        Assert.Equal("the heating's broken again", ApplyWith(s, "the heating broken again", "the heating's broken again"));
    }

    private static readonly FakeSpeller Garble = new("i", "I", "read", "the", "forecast", "says", "it", "will", "rain", "so", "we",
        "went", "over", "to", "bus", "and", "caught", "a", "train", "station", "at", "for", "would've", "been", "maybe", "date", "have");

    [Theory]
    [InlineData("i read gorecastsays it will rain", "i read the forecast says it will rain")]   // merge + dropped "the"
    [InlineData("we went ovwared to the bus", "we went over to the bus")]                       // first letter kept, 0.57 apart
    [InlineData("we caught t train", "we caught the train")]                                   // a stray letter
    [InlineData("so n we went", "so and we went")]
    public void R4_5_a_ANonWordIsFixedEvenWhenFarFromTheFix_D36(string typed, string model) =>
        Assert.Equal(model, ApplyWith(Garble, typed, model));

    [Fact]
    public void R4_5_d_ABadlyGarbledSentenceIsFixedNotRejectedWhole_D36()
    {
        // Found by the stress set: more than half the words were misspelt, so 4.5.d rejected every fix in the message.
        const string typed = "i raed teh forcast sasy it wil rain", model = "i read the forecast says it will rain";
        Assert.Equal(model, ApplyWith(Garble, typed, model));
    }

    [Theory]
    [InlineData("we saw the date", "we saw the the")]                   // a real word keeps the strict rule
    [InlineData("would've hve been", "would've maybe been")]            // different first letter and 0.6 apart: a guess
    [InlineData("we went to the stationfor seven", "we went to the station at seven")]   // D26: no hidden word swap
    [InlineData("we saw Zetters today", "we saw Settle today")]        // capitalised: treated as a name
    public void R4_4_d_NonWordLeniencyStillRefusesGuessesAndRealWords_D36(string typed, string model) =>
        Assert.Equal(typed, ApplyWith(Garble, typed, model));

    [Fact]
    public void R4_4_d_ASpaceNeverSplitsARealWord_D37()
    {
        // User report: "inorganic" became "in organic". Every letter kept, meaning changed.
        var s = new FakeSpeller("the", "inorganic", "in", "organic", "samples", "on", "your", "way");
        Assert.Equal("the inorganic samples", ApplyWith(s, "the inorganic samples", "the in organic samples"));
        Assert.Equal("on your way", ApplyWith(s, "onyour way", "on your way"));   // a run-together non-word is still split
    }

    [Fact]
    public void R4_4_d_ARealWordIsSplitOnlyWhenThePairIsFarMoreCommon_D37()
    {
        // "anew" → "a new" is a missing space (the pair is 500× as common); "inorganic" → "in organic" is not.
        var s = new FakeSpeller("buy", "anew", "a", "new", "mattress", "the", "inorganic", "in", "organic", "samples");
        var pairs = new Dictionary<(string, string), long> { [("a", "new")] = 8108, [("in", "organic")] = 0 };
        var words = new Dictionary<string, long> { ["anew"] = 14, ["inorganic"] = 20 };
        var rules = new EditRules(new EditRuleSettings(), s, _ => false)
        {
            PairCount = (a, b) => pairs.GetValueOrDefault((a, b)),
            WordCount = w => words.GetValueOrDefault(w),
        };
        string Fix(string a, string b) => Apply(a, rules.Filter(a, b, WordDiff.Compute(a, b)));
        Assert.Equal("buy a new mattress", Fix("buy anew mattress", "buy a new mattress"));
        Assert.Equal("the inorganic samples", Fix("the inorganic samples", "the in organic samples"));
    }

    [Theory]
    [InlineData("liaise with theServicees team", "liaise with the Services team")]   // the user's Intercom reply
    [InlineData("send it to aCustomer today", "send it to a customer today")]
    public void R4_5_a_AnArticleRunIntoACapitalisedWordIsAMissedSpace_D54(string typed, string model)
    {
        var s = new FakeSpeller("liaise", "with", "the", "services", "team", "send", "it", "to", "a", "customer", "today");
        Assert.Equal(model, ApplyWith(s, typed, model));
    }

    [Theory]
    [InlineData("call toStrng first", "call to String first")]
    [InlineData("set isValdi here", "set is Valid here")]
    [InlineData("the theValueFeild field", "the the Value Field field")]   // an article, but more than one capital
    public void R4_5_a_CodeLikeWordsStayProtected_D54(string typed, string model)
    {
        // D54 only excuses an article run into one capitalised word; identifiers like these are still never respelt.
        var s = new FakeSpeller("call", "to", "string", "first", "set", "is", "valid", "here", "the", "value", "field");
        Assert.Equal(typed, ApplyWith(s, typed, model));
    }

    [Theory]
    [InlineData("so quickly aboutkitchen quote", "so quickly about the kitchen quote")]   // a small word dropped inside the run
    [InlineData("I'm not surelast paragraph", "I'm not sure the last paragraph")]
    [InlineData("I thinkI will go", "I think I will go")]                                 // "thinkI" is not code
    public void R4_5_a_ARunTogetherNonWordIsSplitEvenWithADroppedSmallWord_D48(string typed, string model)
    {
        // Found on the realistic stress set: the model fixed these, and the rules refused them.
        var s = new FakeSpeller("so", "quickly", "about", "the", "kitchen", "quote", "I", "I'm", "not", "sure", "last", "paragraph", "think", "will", "go");
        Assert.Equal(model, ApplyWith(s, typed, model));
    }

    [Theory]
    [InlineData("we went to the stationfor seven", "we went to the station at seven")]   // a word swap still can't hide in a split
    public void R4_5_a_SplitsStillCannotHideAWordSwap_D48(string typed, string model) =>
        Assert.Equal(typed, ApplyWith(new FakeSpeller("we", "went", "to", "the", "station", "at", "for", "seven", "send", "it", "my", "var", "now"), typed, model));

    [Fact]
    public void R4_5_e_CautiousTurnsOffNonWordLeniency_D36()
    {
        const string typed = "we went ovwared to the bus", model = "we went over to the bus";
        var r = new EditRules(new EditRuleSettings { LenientWithNonWords = false }, Garble, _ => false).Filter(typed, model, WordDiff.Compute(typed, model));
        Assert.Equal(typed, Apply(typed, r));
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
        // Real words changed (a rewrite), not misspellings fixed: D36 doesn't count fixes of non-words.
        var r = Run("I feel happy today", "you see glad me");   // 4 of 4 real words
        Assert.Equal(RejectReason.TooManyChanges, r.WholeRejection);
        Assert.Equal(RejectReason.None, Run("teh cta sta on mat", "the cat sat on mat").WholeRejection);   // 3 typos of 5
    }

    [Fact]
    public void R4_5_e_SettingsAreConfigurable()
    {
        var strict = new EditRuleSettings { MaxNormalisedDistance = 0.2, MaxChangedWordRatio = 0.9, AcceptLikelyKeyboardSlips = false, LenientWithNonWords = false };
        Assert.Empty(Run("teh cat", "the cat", strict).Accepted);           // 0.33 > 0.2
        Assert.Single(Run("teh cat", "the cat").Accepted);
        var tight = new EditRuleSettings { MaxChangedWordRatio = 0.1 };
        Assert.Equal(RejectReason.TooManyChanges, Run("the cat sat on the mat", "the cat sat at the mat", tight).WholeRejection);
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

    [Fact]
    public void R4_4_c_RespacesRunTogetherWordsThatLookCodeLike()
    {
        const string a = "see you nextFriday at the event";
        Assert.Equal("see you next Friday at the event", Apply(a, Run(a, "see you next Friday at the event")));
    }

    [Fact]
    public void R4_4_d_RejectsCapitalisingOrdinaryWordsMidSentence()
    {
        const string a = "apply for the senior analyst job in london";
        Assert.Equal("apply for the senior analyst job in London", Apply(a, Run(a, "apply for the Senior Analyst job in London")));
    }

    [Fact]
    public void R4_5_a_CapitalisesSentenceStartsAndI()
    {
        const string a = "see you. i am going home";
        Assert.Equal("See you. I am going home", Apply(a, Run(a, "See you. I am going home")));
    }

    [Fact]
    public void R4_5_a_SplitWordsMustBeNearlyExact()
    {
        const string a = "book a taxi to the stationfor 7 today";
        Assert.Equal(a, Apply(a, Run(a, "book a taxi to the station at 7 today")));   // "for" → "at" hidden in a split
        const string b = "the policy isno the intranet";
        Assert.Equal("the policy is on the intranet", Apply(b, Run(b, "the policy is on the intranet")));
    }

    [Fact]
    public void R4_5_a_OneLikelyKeyboardSlipIsAcceptedEvenInAOneLetterWord()
    {
        const string a = "book q taxi";
        Assert.Equal("book a taxi", Apply(a, Run(a, "book a taxi")));       // q and a are neighbouring keys
        Assert.Equal(a, Apply(a, Run(a, "book I taxi")));                   // q and I are not
    }

    [Fact]
    public void R4_5_d_NeverRespacesNumbers()
    {
        Assert.Equal(RejectReason.ProtectedTokenChanged, Run("meet at 9am today", "meet at 9 am today").WholeRejection);
    }
}
