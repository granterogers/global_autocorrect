using System.Text.Json;
using System.Text.RegularExpressions;
using GlobalAutocorrect.Core.Health;
using GlobalAutocorrect.Core.Learning;
using GlobalAutocorrect.Core.Tests.EvalData;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Learning;

/// <summary>
/// D179 (user: "as bugs are automatically squashed on their side, improvements are sent to the mother code... I guess
/// this would need approval"): what an install shares, and what is made of it before anyone approves it.
/// </summary>
public sealed class ShareLearningTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static Dictionary<string, AppStats> Apps(params (string Name, double Writes)[] apps) =>
        apps.ToDictionary(a => a.Name, a => new AppStats { Writes = a.Writes, Undos = 1 }, StringComparer.OrdinalIgnoreCase);

    private static ShareLearning.Submission Sub(string id, Dictionary<string, AppStats>? apps = null, params string[] marks) =>
        ShareLearning.Build(id, Key, "1.50.0", apps ?? Apps(("notepad", 10)), marks.ToDictionary(m => m, _ => 1));

    [Fact]
    public void R8_6_D179_WhatIsSentHoldsCountsAndFingerprintsOnly()
    {
        var apps = Apps(("WINWORD", 40), ("My Report.docx", 9));
        apps["idle"] = new AppStats();
        var s = ShareLearning.Build("a1b2c3d4e5f6", Key, "1.50.0", apps, new Dictionary<string, int> { ["0123456789abcdef"] = 2, ["teh→the"] = 1 });

        Assert.Equal(["winword"], s.Apps.Keys);                    // lower case; a file-like name and an unused program left out
        Assert.Equal(["0123456789abcdef"], s.Mistakes.Keys);       // a fingerprint, never words
        string json = ShareLearning.ToJson(s);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["format", "id", "key", "version", "apps", "mistakes"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.All(doc.RootElement.GetProperty("apps").GetProperty("winword").EnumerateObject(), p => Assert.Equal(JsonValueKind.Number, p.Value.ValueKind));
        Assert.NotNull(ShareLearning.Parse(json));                 // what is sent passes the receivers' check
        Assert.Equal("1 program and 1 undone change", ShareLearning.Describe(s));
    }

    [Fact]
    public void R8_6_D179_AFieldThatDoesntBelongRefusesTheWholeSubmission()
    {
        static System.Text.Json.Nodes.JsonObject Good() => System.Text.Json.Nodes.JsonNode.Parse(ShareLearning.ToJson(Sub("a1b2c3d4e5f6")))!.AsObject();
        var top = Good();
        top["text"] = "hello world";                               // a field nobody asked for
        Assert.Null(ShareLearning.Parse(top.ToJsonString()));
        var stat = Good();
        stat["apps"]!["notepad"]!["typed"] = 3;                    // a count the app doesn't keep
        Assert.Null(ShareLearning.Parse(stat.ToJsonString()));
        Assert.NotNull(ShareLearning.Parse(Good().ToJsonString()));
    }

    [Fact]
    public void R8_6_D179_CountsNamesAndIdsThatDontFitRefuseTheWholeSubmission()
    {
        string good = ShareLearning.ToJson(Sub("a1b2c3d4e5f6", null, "0123456789abcdef"));
        Assert.NotNull(ShareLearning.Parse(good));
        Assert.Null(ShareLearning.Parse(good.Replace("\"writes\": 10", "\"writes\": -1")));
        Assert.Null(ShareLearning.Parse(good.Replace("\"writes\": 10", "\"writes\": 1000000000")));   // trying to outweigh everyone
        Assert.Null(ShareLearning.Parse(good.Replace("\"notepad\"", "\"My Report.docx\"")));
        Assert.Null(ShareLearning.Parse(good.Replace("0123456789abcdef\"", "teh to the\"")));
        Assert.Null(ShareLearning.Parse(good.Replace("a1b2c3d4e5f6", "c-a1b2c3d4")));                 // the brain's own prefix
        Assert.Null(ShareLearning.Parse(good.Replace(Key, "short")));
        Assert.NotNull(ShareLearning.Parse(good.Replace(Key, ""), needKey: false));                   // as the relay hands it on
        Assert.Null(ShareLearning.Parse(good.Replace(Key, ""), needKey: true));
        Assert.Null(ShareLearning.Parse(new string(' ', ShareLearning.MaxBytes + 1)));
    }

    [Fact]
    public void D179_TheRelayChecksTheSameCountsAndLimitsAsTheApp()
    {
        string js = File.ReadAllText(Path.Combine(EvalDataFiles.RepoRoot(), "relay", "learning.mjs"));
        var relay = Regex.Match(js, @"export const STATS = \[(.*?)\];", RegexOptions.Singleline).Groups[1].Value;
        var relayStats = Regex.Matches(relay, "'([a-zA-Z]+)'").Select(m => m.Groups[1].Value).Order();
        var appStats = typeof(AppStats).GetProperties().Where(p => p.PropertyType == typeof(double) && p.CanWrite)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Order();
        Assert.Equal(appStats, relayStats);
        Assert.Contains($"MAX_APPS = {ShareLearning.MaxApps};", js);
        Assert.Contains($"MAX_MISTAKES = {ShareLearning.MaxMistakes};", js);
        Assert.Contains($"MAX_COUNT = {ShareLearning.MaxCount};", js);
    }

    [Fact]
    public void D179_OnlyChangesSeveralInstallsUndidReachEveryPc()
    {
        const string a = "aaaaaaaaaaaaaaaa", b = "bbbbbbbbbbbbbbbb";
        var subs = new[]
        {
            Sub("install-0001", null, a, b), Sub("install-0002", null, a, b), Sub("install-0003", null, a),
            Sub("6b0e9443ffcf", null, b),     // a PC that publishes itself: already counted
        };
        var f = CommunityLearning.Fold(subs, new HashSet<string> { "6b0e9443ffcf" });
        Assert.Equal(new Dictionary<string, int> { [a] = 3 }, f.Mistakes);   // b: only 2 installs (one install alone can't block a fix)
        Assert.Equal(["install-0001", "install-0002", "install-0003"], f.Installs.Keys.Order());
        Assert.Equal(1, f.SkippedOwn);
    }

    [Fact]
    public void D179_EachInstallKeepsOnlyItsBusiestProgramsSoTheBrainStaysSmall()
    {
        var many = Apps(Enumerable.Range(1, 40).Select(i => ($"app{i:00}", (double)i)).ToArray());
        var f = CommunityLearning.Fold([Sub("install-0001", many)], new HashSet<string>());
        var kept = f.Installs["install-0001"].Apps.Keys;
        Assert.Equal(CommunityLearning.ProgramsPerInstall, kept.Count);
        Assert.Contains("app40", kept);
        Assert.DoesNotContain("app01", kept);
        Assert.Equal("", f.Installs["install-0001"].Updated);   // no date: an unchanged share changes nothing to approve
    }

    [Fact]
    public void R8_6_D179_D103_AnInstallThatSharesLeavesItsOwnCountsOut()
    {
        var b = new BrainFile
        {
            Pcs = new()
            {
                ["c-a1b2c3d4e5f6"] = Apps(("notepad", 50)),
                ["6b0e9443ffcf"] = Apps(("notepad", 7)),
            },
        };
        var elsewhere = Brain.LearntElsewhere(b, "a1b2c3d4e5f6", new DateOnly(2026, 11, 24));
        Assert.Equal(7, elsewhere["notepad"].Writes);
    }

    [Fact]
    public void D179_TheBrainNamesTheRelayOnlyByAnHttpsAddress()
    {
        static BrainFile? With(string url) => Brain.Parse($$"""{ "format": 1, "updated": "2026-11-24T12:00:00Z", "share": { "url": "{{url}}" } }""");
        Assert.Equal("https://ga-learning.example.workers.dev/v1/learning", With("https://ga-learning.example.workers.dev/v1/learning")!.Share!.Url);
        Assert.Null(With("http://ga-learning.example.workers.dev/v1/learning")!.Share);
        Assert.Null(With("not an address")!.Share);
    }

    [Fact]
    public void D179_TheApproverIsToldWhatWouldChange()
    {
        var old = new BrainFile { Apps = Apps(("notepad", 10)), Mistakes = new() { ["aaaaaaaaaaaaaaaa"] = 2 } };
        var next = new BrainFile { Apps = Apps(("notepad", 30), ("winword", 12)), Mistakes = new() { ["aaaaaaaaaaaaaaaa"] = 2, ["bbbbbbbbbbbbbbbb"] = 3 } };
        string s = CommunityLearning.Summarise(old, next, installs: 4, refused: 1, skippedOwn: 0);
        Assert.Contains("Installs sharing: **4** (1 refused by the checks)", s);
        Assert.Contains("Undone changes every PC avoids: 1 → 2", s);
        Assert.Contains("winword (new:", s);
        Assert.Contains("notepad (10 written", s);
    }
}
