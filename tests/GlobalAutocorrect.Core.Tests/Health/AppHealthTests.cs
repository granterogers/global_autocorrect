using GlobalAutocorrect.Core.Health;
using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Health;

/// <summary>D81: the app learns per program how its fixes go (counts only) and adapts; lessons are published to the source.</summary>
public sealed class AppHealthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ga-health-" + Guid.NewGuid().ToString("N"));

    public AppHealthTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void R4_6_d_D81_AFieldThatKeepsDisagreeingIsGivenUpOnOnce_NotRetriedEveryTick()
    {
        var h = new Harness();
        var health = new AppHealth();
        h.Engine.Health = health;
        h.Ed.AppName = "claude";
        string? stuck = null;
        h.Engine.Stuck += app => stuck = app;
        h.Type("I saw teh");
        // From now on the field reports an object at the caret: it never matches the record and never ends with the last
        // key, so no resync can be adopted (as the Claude app did 340 times in a day).
        h.Ed.DistortBefore = s => s + "￼";
        h.Type(" ");
        h.Wait(3000);

        Assert.Equal("claude", stuck);
        Assert.Equal(1, h.Counters.FieldMismatches);           // logged once, not every 25 ms
        Assert.Equal("stuck", h.Engine.LastDropReason);
        Assert.Equal(1, health.StatsFor("claude").Stuck);
        Assert.Equal("I saw teh ", h.Text);                    // nothing was written into a field we can't read right
    }

    /// <summary>D89, user request "if it happens once, it never happens again" (WhatsApp, 2026-11-04: one AI fix stepped
    /// to with arrows landed wrong, and the rest of the message was left).</summary>
    [Fact]
    public void R4_6_d_D89_AnArrowFixThatWentWrongOnceIsRetypedFromThenOn()
    {
        var h = new Harness(model: s => s.Replace("went their", "went there"), latencyMs: 50);
        var health = new AppHealth();
        h.Engine.Health = health;
        var learnt = new List<string>();
        h.Engine.LearntFromMistake += learnt.Add;
        h.Ed.AppName = "WhatsApp.Root";
        h.Ed.ArrowsAppliedLate = true;

        h.Type("We went their yesterday. ");
        h.Wait(1500);
        Assert.Equal(1, h.Counters.Desyncs);                                 // the first one went wrong, as in WhatsApp
        Assert.Contains("arrows-failed", learnt);
        Assert.True(health.RetypeIn("whatsapp.root"));

        h.Type("They went their today. ");                                     // the app carries on from what the field shows
        h.Wait(1500);
        Assert.EndsWith("They went there today. ", h.Text);                  // retyped: no arrow keys to go wrong
        Assert.Equal(1, h.Counters.Desyncs);
    }

    [Fact]
    public void R4_6_d_D89_TheLessonFadesAfterAboutTwoWeeksAndIsShared()
    {
        var day = new DateOnly(2026, 11, 4);
        var a = new AppHealth(Path.Combine(_dir, "apphealth.json"), () => day);
        a.ArrowsFailed("WhatsApp.Root");
        Assert.True(a.RetypeIn("WhatsApp.Root"));
        Assert.False(a.RetypeIn("notepad"));
        Assert.Contains("whatsapp.root", AppHealth.Combine([a.Local], day).Apps.Keys);   // one failure is enough to share
        day = day.AddDays(12);
        Assert.True(a.RetypeIn("WhatsApp.Root"));
        day = day.AddDays(2);
        Assert.False(a.RetypeIn("WhatsApp.Root"));
    }

    [Fact]
    public void R4_6_d_D81_AReadThatOnlyLagsIsNotStuck()
    {
        var h = new Harness();
        var health = new AppHealth();
        h.Engine.Health = health;
        h.Ed.AppName = "msedge";
        h.Type("I saw teh");
        h.Ed.LagNextReads = 3;   // a browser catching up: behind, not different
        h.Type(" ");
        h.Wait(500);
        Assert.Equal("I saw the ", h.Text);
        Assert.Equal(0, health.StatsFor("msedge").Stuck);
        Assert.Equal(1, health.StatsFor("msedge").Writes);
    }

    [Fact]
    public void R8_6_D81_CountsAreSavedFadeEachDayAndHoldNoText()
    {
        var day = new DateOnly(2026, 11, 4);
        string path = Path.Combine(_dir, "apphealth.json");
        var a = new AppHealth(path, () => day);
        for (int i = 0; i < 10; i++) a.Written("Teams");
        a.VerifyFailed("teams");
        a.Save();

        string json = File.ReadAllText(path);
        Assert.Contains("\"Writes\": 10", json);
        Assert.DoesNotContain("teh", json);

        day = day.AddDays(7);
        var b = new AppHealth(path, () => day);
        b.Load();
        Assert.Equal(10 * Math.Pow(AppHealth.DailyFade, 7), b.StatsFor("TEAMS").Writes, 6);   // names ignore case
    }

    [Fact]
    public void R13_D81_ShippedProfilesAreAddedToLocalCounts_AndCombineKeepsOnlyProgramsWithEnoughEvidence()
    {
        string shipped = Path.Combine(_dir, "app-profiles.json");
        File.WriteAllText(shipped, AppHealth.ToJson(new AppHealth.ProfileFile("2026-11-01",
            new() { ["claude"] = new AppStats { Writes = 2, Stuck = 6 } })));
        var h = new AppHealth();
        h.Load(shipped);
        h.Stuck("claude");
        Assert.Equal(7, h.StatsFor("claude").Stuck);

        h.Written("notepad");   // one event on one PC: too little to publish
        var otherPc = new Dictionary<string, AppStats> { ["Claude"] = new() { Stuck = 5 }, ["notepad"] = new() { Writes = 1 } };
        var combined = AppHealth.Combine([h.Local, otherPc], new DateOnly(2026, 11, 4));
        Assert.Equal(["claude"], combined.Apps.Keys);
        Assert.Equal(6, combined.Apps["claude"].Stuck);   // 1 here + 5 there; the shipped file isn't added again
        Assert.Equal("2026-11-04", combined.Updated);
    }
}
