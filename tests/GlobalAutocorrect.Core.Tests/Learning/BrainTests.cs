using System.Text.Json;
using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Health;
using GlobalAutocorrect.Core.Learning;
using GlobalAutocorrect.Core.Settings;
using GlobalAutocorrect.Core.Tests.EvalData;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Learning;

/// <summary>D102, user request: "the brains of the program are adaptable without needing to recompile it ... update this
/// brain online ... the binary will frequently look for any updates to the brain".</summary>
public sealed class BrainTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ga-brain-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Json(string updated, string tuning = "{}", string write = "{}", int format = 1) => $$"""
        { "format": {{format}}, "updated": "{{updated}}",
          "apps": { "notepad": { "writes": 3, "arrowFailures": 1 } },
          "mistakes": { "0123456789abcdef": 2, "not-a-fingerprint": 2 },
          "tuning": {{tuning}}, "write": {{write}} }
        """;

    [Fact]
    public void R4_6_d_D102_TheRepositorysBrainLoadsAndEveryValueIsOneTheAppKnows()
    {
        string path = Path.Combine(EvalDataFiles.RepoRoot(), "brain", "brain.json");
        string text = File.ReadAllText(path);
        var b = Brain.Parse(text);
        Assert.NotNull(b);
        Assert.NotEqual(DateTimeOffset.MinValue, b.UpdatedAt);
        // Nothing in the file was dropped: a misspelt knob or write rule would silently do nothing.
        using var raw = JsonDocument.Parse(text);
        int Count(string name) => raw.RootElement.TryGetProperty(name, out var e) ? e.EnumerateObject().Count() : 0;
        Assert.Equal(Count("tuning"), b.Tuning.Count);
        Assert.Equal(Count("write"), b.Write.Count);
        Assert.Equal(Count("mistakes"), b.Mistakes.Count);
        Assert.Equal(Count("apps"), b.Apps.Count);
        foreach (var (name, value) in b.Tuning)
            Assert.InRange(value, Brain.Knobs[name].Min, Brain.Knobs[name].Max);
    }

    [Fact]
    public void R4_6_d_D102_ABrainIsCheckedAndWhatDoesntBelongIsLeftOut()
    {
        var b = Brain.Parse(Json("2026-11-09T12:00:00Z", """{ "WriteGapMs": 200, "NoSuchKnob": 5 }""", """{ "msedge": "retype", "x": "sideways" }"""))!;
        Assert.Equal(["0123456789abcdef"], b.Mistakes.Keys);
        Assert.Equal(["WriteGapMs"], b.Tuning.Keys);
        Assert.Equal(["msedge"], b.Write.Keys);
        Assert.Equal(3, b.Apps["NOTEPAD"].Writes);

        Assert.Null(Brain.Parse(Json("2026-11-09T12:00:00Z", format: 2)));   // needs a newer app
        Assert.Null(Brain.Parse("not json"));
        Assert.NotNull(Brain.Parse("""{ "format": 1, "apps": { "a": { "writes": 1 }, "A": { "writes": 2 } } }"""));   // no throw on case twins
    }

    [Fact]
    public void R4_6_d_D102_TuningIsHeldToItsRangeAndTheUsersSettingsStillWin()
    {
        var tuned = Brain.Tune(new EngineOptions(), new Dictionary<string, double> { ["WriteGapMs"] = 99_999, ["ShakyAppGapMs"] = 800, ["RevisitAfterMs"] = 2000 });
        Assert.Equal(1000, tuned.WriteGapMs);
        Assert.Equal(800, tuned.ShakyAppGapMs);
        var user = new AppSettings { LookBackWhenPaused = false }.ToEngineOptions(tuned);
        Assert.Equal(0, user.RevisitAfterMs);   // the user turned look-back off; the brain can't turn it on
    }

    [Fact]
    public void R4_6_d_D102_TheBrainCanSayHowToWriteInAProgramWhateverTheCounts()
    {
        var health = new AppHealth();
        health.ArrowsFailed("notepad");
        Assert.True(health.RetypeIn("notepad"));
        health.UseShipped(new Dictionary<string, AppStats>(), new Dictionary<string, string> { ["Notepad"] = "arrows", ["winword"] = "retype" });
        Assert.False(health.RetypeIn("notepad"));
        Assert.True(health.RetypeIn("WINWORD"));
        Assert.True(health.ArrowsShaky("winword"));
    }

    [Fact]
    public async Task R9_3_g_D102_ANewerBrainIsDownloadedSavedAndAppliedWhileRunning()
    {
        string cache = Path.Combine(_dir, "brain.json"), shipped = Path.Combine(_dir, "shipped.json");
        File.WriteAllText(shipped, Json("2026-11-01T00:00:00Z"));
        string served = Json("2026-11-09T12:00:00Z", """{ "WriteGapMs": 200 }""");
        string? sentEtag = null;
        bool allowed = true;
        var monitor = new BrainMonitor(cache, shipped, (etag, _) =>
        {
            sentEtag = etag;
            return Task.FromResult<BrainDownload?>(etag == "\"v2\"" ? new BrainDownload(true, null, etag) : new BrainDownload(false, served, "\"v2\""));
        }, () => allowed);
        BrainFile? applied = null;
        monitor.Changed += b => applied = b;

        Assert.Equal("2026-11-01T00:00:00Z", monitor.LoadLocal().Updated);
        Assert.True(await monitor.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(200, applied!.Tuning["WriteGapMs"]);
        Assert.Equal("2026-11-09T12:00:00Z", Brain.Read(cache)!.Updated);

        Assert.False(await monitor.CheckAsync(TestContext.Current.CancellationToken));   // unchanged: the ETag is sent and nothing is downloaded again
        Assert.Equal("\"v2\"", sentEtag);

        // After a restart the downloaded brain is used, being later than the shipped one.
        var restarted = new BrainMonitor(cache, shipped, (_, _) => Task.FromResult<BrainDownload?>(null), () => true);
        Assert.Equal("2026-11-09T12:00:00Z", restarted.LoadLocal().Updated);

        allowed = false;   // "Check for updates" off: nothing is fetched
        sentEtag = "untouched";
        Assert.False(await monitor.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal("untouched", sentEtag);
    }

    [Fact]
    public async Task R9_3_g_D102_AnOlderOrUnusableBrainIsNeverTaken()
    {
        string cache = Path.Combine(_dir, "brain.json"), shipped = Path.Combine(_dir, "shipped.json");
        File.WriteAllText(shipped, Json("2026-11-09T00:00:00Z"));
        string served = Json("2026-11-01T00:00:00Z");
        var monitor = new BrainMonitor(cache, shipped, (_, _) => Task.FromResult<BrainDownload?>(new BrainDownload(false, served, null)), () => true);
        monitor.LoadLocal();
        Assert.False(await monitor.CheckAsync(TestContext.Current.CancellationToken));   // older than the one shipped with this build
        served = Json("2026-11-20T00:00:00Z", format: 2);
        Assert.False(await monitor.CheckAsync(TestContext.Current.CancellationToken));   // a format this build doesn't know
        Assert.False(File.Exists(cache));
        Assert.Equal("2026-11-09T00:00:00Z", monitor.Current.Updated);
    }
}
