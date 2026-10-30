using System.Text.RegularExpressions;
using GlobalAutocorrect.Core.Prediction;
using GlobalAutocorrect.Core.Tests.Instant;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Prediction;

/// <summary>D57: the history also learns per app, and prediction leans towards what was typed in the app in use.</summary>
public sealed class PerAppPredictionTests : IDisposable
{
    private static readonly Lazy<NgramModel> Model = new(() => NgramModel.FromAssets(HunspellTests.Assets()));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ga-hist-" + Guid.NewGuid().ToString("N"));

    private string HistoryPath => Path.Combine(_dir, "history.txt");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>What the engine reports as each word is finished (D40), from the given app.</summary>
    private static void Learn(UserHistory h, string sentence, string? app)
    {
        string? w1 = null, w2 = UserHistory.SentenceStart;
        foreach (var w in sentence.Split(' '))
        {
            h.Observe(w, app);
            h.ObservePhrase(w1, w2, w, app);
            (w1, w2) = (w2, w.ToLowerInvariant());
        }
    }

    [Fact]
    public void R7_3_TheCurrentAppsOwnPhrasingIsPredicted_D57()
    {
        // Typed "ping the build" three times in Teams and "ping the customer" twice in Intercom's browser: across all
        // apps neither is a clear favourite, so nothing is shown; in Teams, Teams' own phrasing is.
        var history = new UserHistory(HistoryPath);
        for (int i = 0; i < 3; i++) Learn(history, "ping the build", "ms-teams");
        for (int i = 0; i < 2; i++) Learn(history, "ping the customer", "msedge");
        var predictor = new Predictor(Model.Value, history);

        Assert.NotEqual("build", predictor.Suggest("Ping the ")?.Word);
        Assert.Equal("build", predictor.Suggest("Ping the ", "ms-teams")?.Word);
        Assert.NotEqual("build", predictor.Suggest("Ping the ", "msedge")?.Word);
        // An app nothing was typed in yet uses the history as a whole.
        Assert.Equal(predictor.Suggest("Ping the ")?.Word, predictor.Suggest("Ping the ", "notepad")?.Word);
        // Weight 1 is per-app learning off.
        Assert.NotEqual("build", new Predictor(Model.Value, history) { CurrentAppWeight = 1 }.Suggest("Ping the ", "ms-teams")?.Word);
    }

    [Fact]
    public void R7_3_PerAppLearningNeverChangesTheGlobalCounts_D57()
    {
        var history = new UserHistory(HistoryPath);
        Learn(history, "how are you doing sir", "ms-teams");
        Learn(history, "how are you doing", null);
        Assert.Equal(2, history.Count("doing"));
        Assert.Equal(1, history.CountIn("MS-Teams", "doing"));
        Assert.Equal(0, history.CountIn("notepad", "doing"));
        Assert.Equal(0, history.CountIn(null, "doing"));
        Assert.Equal((9L, 5L), history.Totals("ms-teams"));
        Assert.Equal("doing", new Predictor(Model.Value, history).Suggest("How are you ")?.Word);
    }

    [Fact]
    public void R7_3_PerAppCountsSurviveARestartWithoutCountingTwice_D57()
    {
        var history = new UserHistory(HistoryPath);
        for (int i = 0; i < 3; i++) Learn(history, "ping the build", "ms-teams");
        Learn(history, "ping the customer", "msedge");
        history.Save();

        var lines = File.ReadAllLines(HistoryPath);
        foreach (var line in lines)   // global lines exactly as before (D40), then "@app<TAB>" lines: words only, never sentences
            Assert.Matches(new Regex(@"^(@[a-z0-9._-]+\t)?(<s> )?[a-z']+( [a-z']+){0,2}\t\d+$"), line);
        Assert.Contains("@ms-teams\tbuild\t3", lines);
        Assert.Contains("@msedge\tping the customer\t1", lines);
        int firstApp = Array.FindIndex(lines, l => l.StartsWith('@'));
        Assert.True(firstApp > 0 && lines[firstApp..].All(l => l.StartsWith('@')), "per-app lines come after the global ones");

        var reloaded = new UserHistory(HistoryPath);
        Assert.Equal(4, reloaded.Count("ping"));               // not 8: per-app lines are a share, not more uses
        Assert.Equal(3, reloaded.CountIn("ms-teams", "build"));
        Assert.Equal(["ms-teams", "msedge"], reloaded.Apps.Order(StringComparer.Ordinal));
        Assert.Equal("build", new Predictor(Model.Value, reloaded).Suggest("Ping the ", "ms-teams")?.Word);
    }

    [Fact]
    public void R7_3_AHistoryFileFromBeforePerAppLearningStillLoads_D57()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(HistoryPath, "doing\t2\nhow\t2\n<s> how\t2\nhow are\t2\nare you\t2\nhow are you\t2\nyou doing\t2\nare you doing\t2\n");
        var history = new UserHistory(HistoryPath);
        Assert.Equal(2, history.Count("doing"));
        Assert.Empty(history.Apps);
        Assert.Equal("doing", new Predictor(Model.Value, history).Suggest("How are you ", "ms-teams")?.Word);

        // Lines a newer version might write that this one can't read are skipped, not taken as global counts.
        File.AppendAllText(HistoryPath, "@\tdoing\t5\n@bad name\tdoing\t5\n@ok\tdoing\tmany\n");
        var again = new UserHistory(HistoryPath);
        Assert.Equal(2, again.Count("doing"));
        Assert.Empty(again.Apps);
    }

    [Fact]
    public void R7_3_PerAppHistoryIsBounded_D57()
    {
        var history = new UserHistory(HistoryPath);
        for (int app = 0; app < 25; app++)
            for (int n = 0; n <= app; n++) history.Observe("hello", $"app{app}");   // app24 is the most used
        // 70,000 different one-off words in one app, and one word used often.
        for (int i = 0; i < 70_000; i++) history.Observe(Letters(i), "notepad");
        for (int i = 0; i < 5; i++) history.Observe("rota", "notepad");
        history.Save();

        Assert.Equal(20, history.Apps.Count);
        Assert.DoesNotContain("app0", history.Apps);   // the least used apps go first
        Assert.Contains("app24", history.Apps);
        Assert.Contains("notepad", history.Apps);
        var appLines = File.ReadLines(HistoryPath).Count(l => l.StartsWith('@'));
        Assert.True(appLines <= 60_000, $"{appLines} per-app lines");
        Assert.Equal(5, history.CountIn("notepad", "rota"));   // past the cap, one-offs go first
        // The global counts are kept as before: "hello", "rota" and the 70,000 words.
        Assert.Equal(70_002, File.ReadLines(HistoryPath).Count(l => !l.StartsWith('@')));
        Assert.Equal(1, history.Count(Letters(69_999)));

        static string Letters(int i)
        {
            var s = "";
            for (i += 26 * 27; i > 0; i /= 26) s = (char)('a' + i % 26) + s;
            return s;
        }
    }

    [Theory]
    [InlineData("MS-Teams", "ms-teams")]
    [InlineData("olk", "olk")]
    [InlineData("bad\tname\nx", "badnamex")]
    [InlineData("?", null)]
    [InlineData("   ", null)]
    public void R7_3_AppNamesAreStoredAsPlainProcessNames_D57(string app, string? stored)
    {
        var history = new UserHistory(HistoryPath);
        history.Observe("hello", app);
        Assert.Equal(stored is null ? [] : [stored], history.Apps);
        history.Save();
        foreach (var line in File.ReadLines(HistoryPath)) Assert.Equal(line.StartsWith('@') ? 3 : 2, line.Split('\t').Length);
    }
}
