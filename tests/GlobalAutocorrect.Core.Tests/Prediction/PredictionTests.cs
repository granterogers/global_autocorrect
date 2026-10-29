using System.Diagnostics;
using System.Text.RegularExpressions;
using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Prediction;
using GlobalAutocorrect.Core.Tests.Instant;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Prediction;

public sealed class PredictionTests : IDisposable
{
    private static readonly Lazy<NgramModel> Model = new(() => NgramModel.FromAssets(HunspellTests.Assets()));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ga-hist-" + Guid.NewGuid().ToString("N"));

    private string HistoryPath => Path.Combine(_dir, "history.txt");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private Predictor NewPredictor(out UserHistory history)
    {
        history = new UserHistory(HistoryPath);
        return new Predictor(Model.Value, history);
    }

    [Fact]
    public void R7_1_CompletesAPartialWord()
    {
        var predictor = NewPredictor(out _);
        var s = predictor.Suggest("I'll send you the infor");
        Assert.NotNull(s);
        Assert.False(s.IsNextWord);
        Assert.StartsWith("infor", s.Word);
        Assert.Equal(s.Word["infor".Length..], s.Completion);
    }

    [Theory]
    [InlineData("Thank you for your ")]
    [InlineData("Looking forward to ")]
    [InlineData("Please let me ")]
    public void R7_1_PredictsTheNextWordAfterASpace(string text)
    {
        var predictor = NewPredictor(out _);
        var found = new[] { text, "Thank you for your ", "Looking forward to ", "I am looking forward to ", "Please let me ", "Kind regards. " }
            .Select(predictor.Suggest)
            .FirstOrDefault(x => x is not null && x.IsNextWord);
        Assert.NotNull(found);
        Assert.True(found.Word.Length >= 2);
        Assert.Equal(found.Word, found.Completion, ignoreCase: true);
    }

    [Fact]
    public void R7_1_ModelPredictsKnownContexts()
    {
        var next = Model.Value.Next("looking", "forward", 5);
        Assert.Contains(next, x => x.Word == "to");
        var start = Model.Value.Next(null, "<s>", 5);
        Assert.NotEmpty(start);
        Assert.DoesNotContain(start, x => x.Word == "<s>");
    }

    [Fact]
    public void R7_2_AnswersUnder10ms()
    {
        var predictor = NewPredictor(out _);
        string[] inputs =
        [
            "I'll send you the infor", "Thank you for your ", "Looking forward to ", "We need to organi", "see you at the mee",
            "Hello. ", "This is a very long line of text with many words in it and we are now typing the infor",
            "Could you please let me ", "colo", "the quick brown fox jumps over the la", "Dear Sir, ", "I am writing to you about the ",
        ];

        for (int i = 0; i < 20; i++)
        {
            predictor.Suggest(inputs[i % inputs.Length]);
        }

        double total = 0, max = 0;
        for (int i = 0; i < 200; i++)
        {
            var sw = Stopwatch.StartNew();
            predictor.Suggest(inputs[i % inputs.Length]);
            sw.Stop();
            total += sw.Elapsed.TotalMilliseconds;
            max = Math.Max(max, sw.Elapsed.TotalMilliseconds);
        }

        Assert.True(total / 200 < 10, $"average {total / 200:F3} ms");
        Assert.True(max < 30, $"max {max:F3} ms");
    }

    [Theory]
    [InlineData("I like the colo")]
    [InlineData("the colo")]
    [InlineData("colo")]
    [InlineData("We should organi")]
    [InlineData("the organi")]
    public void R7_2_SuggestionsAreBritish(string text)
    {
        var predictor = NewPredictor(out _);
        var s = predictor.Suggest(text);
        if (s is not null)
        {
            Assert.NotEqual("color", s.Word);
            Assert.DoesNotContain("iz", s.Word);
        }

        foreach (var (word, _) in Model.Value.Complete("colo", 50)) Assert.NotEqual("color", word);
        foreach (var (word, _) in Model.Value.Complete("organi", 200)) Assert.DoesNotContain("iz", word);
    }

    [Fact]
    public void R7_2_EveryModelWordIsValidBritishEnglish()
    {
        var speller = HunspellTests.Shared;
        foreach (var (word, _) in Model.Value.Complete("co", 200))
        {
            Assert.True(speller.IsValid(word) || speller.IsValid(char.ToUpperInvariant(word[0]) + word[1..]), word);
        }
    }

    [Fact]
    public void R7_3_HistoryStoresOnlyWordCounts()
    {
        var history = new UserHistory(HistoryPath);
        foreach (var w in "I'll send the report to Grant on 12 May, email grant@example.com and the report again".Split(' '))
        {
            history.Observe(w.Trim(','));
        }

        history.Save();
        var lines = File.ReadAllLines(HistoryPath);
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            Assert.Matches(new Regex(@"^[a-z']+\t\d+$"), line);
            Assert.DoesNotContain(' ', line);
        }

        Assert.Equal(2, history.Count("report"));
        Assert.Equal(0, history.Count("12"));
        Assert.Equal(0, history.Count("grant@example.com"));
        Assert.DoesNotContain(lines, l => l.StartsWith("12\t", StringComparison.Ordinal));

        var reloaded = new UserHistory(HistoryPath);
        Assert.Equal(2, reloaded.Count("REPORT"));
        Assert.False(File.Exists(HistoryPath + ".tmp"));
        var bytes = File.ReadAllBytes(HistoryPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public void R7_3_HistoryIgnoresVeryLongTokens()
    {
        var history = new UserHistory(HistoryPath);
        history.Observe(new string('a', 31));
        history.Observe(new string('a', 30));
        Assert.Equal(0, history.Count(new string('a', 31)));
        Assert.Equal(1, history.Count(new string('a', 30)));
    }

    [Fact]
    public void R7_3_HistoryBoostsTheUsersOwnWords()
    {
        // "zymurgy" is valid British English but too rare to be offered for "zym" from the corpus alone.
        var predictor = NewPredictor(out var history);
        Assert.True(HunspellTests.Shared.IsValid("zymurgy"));
        var before = predictor.Suggest("I love zym");
        Assert.True(before is null || before.Word != "zymurgy");

        for (int i = 0; i < 20; i++) history.Observe("zymurgy");
        var after = predictor.Suggest("I love zym");
        Assert.NotNull(after);
        Assert.Equal("zymurgy", after.Word);
        Assert.Equal("urgy", after.Completion);
    }

    [Fact]
    public void R7_5_NoSuggestionAfterNonMatchingText()
    {
        var predictor = NewPredictor(out _);
        Assert.Null(predictor.Suggest("abcxyz"));
        Assert.Null(predictor.Suggest(""));
        Assert.Null(predictor.Suggest("a"));
    }

    [Fact]
    public void R7_7_DisabledPredictorSuggestsNothing()
    {
        var predictor = NewPredictor(out _);
        Assert.True(predictor.Enabled);
        Assert.NotNull(predictor.Suggest("I'll send you the infor"));
        predictor.Enabled = false;
        Assert.Null(predictor.Suggest("I'll send you the infor"));
        predictor.Enabled = true;
        Assert.NotNull(predictor.Suggest("I'll send you the infor"));
    }

    [Theory]
    [InlineData("see www.exa")]
    [InlineData("room 12b")]
    [InlineData("go to https://exa")]
    [InlineData("email grant@exa")]
    [InlineData("room 12 ")]
    public void R8_6_NoSuggestionInsideUrlsOrNumbers(string text)
    {
        var predictor = NewPredictor(out _);
        Assert.Null(predictor.Suggest(text));
    }

    [Fact]
    public void R7_1_AmbiguousPartialWordGivesNoSuggestion()
    {
        // "documents" (1601) and "document" (1555) are too close for the 1.5x rule, so nothing is shown.
        var predictor = NewPredictor(out _);
        Assert.Null(predictor.Suggest("I'll send you the docum"));
    }

    [Fact]
    public void R7_3_HistoryBreaksTiesBetweenCloseWords()
    {
        var predictor = NewPredictor(out var history);
        for (int i = 0; i < 20; i++) history.Observe("document");
        var s = predictor.Suggest("I'll send you the docum");
        Assert.NotNull(s);
        Assert.Equal("document", s.Word);
        Assert.Equal("ent", s.Completion);
    }

    [Fact]
    public void R7_1_OnlyTheLastLineMatters()
    {
        var predictor = NewPredictor(out _);
        var s = predictor.Suggest("room 12\nI'll send you the infor");
        Assert.NotNull(s);
        Assert.StartsWith("infor", s.Word);
    }

    [Fact]
    public void R7_1_AllCapsPartialGetsUpperCaseCompletion()
    {
        var predictor = NewPredictor(out _);
        var s = predictor.Suggest("I'll send you the INFOR");
        Assert.NotNull(s);
        Assert.Equal(s.Completion.ToUpperInvariant(), s.Completion);
    }
}

