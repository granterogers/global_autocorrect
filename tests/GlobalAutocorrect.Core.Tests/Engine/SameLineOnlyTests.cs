using GlobalAutocorrect.Core.Health;
using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D121, user report (Intercom: "slimmed-down -downversion"): once a fix on an earlier line has gone wrong in a
/// program, that program gets fixes on the caret's own line only.</summary>
public sealed class SameLineOnlyTests
{
    [Fact]
    public void R4_6_d_D121_AfterAnEarlierLineFixWentWrongOnlyTheCaretsLineIsFixed()
    {
        var health = new AppHealth();
        health.LineFixFailed("msedge");
        Assert.True(health.SameLineOnly("msedge"));
        Assert.False(health.SameLineOnly("notepad"));

        var h = new Harness(model: s => s.Replace("went their", "went there").Replace("teh dog", "the dog"), latencyMs: 50);
        h.Engine.Health = health;
        h.Ed.AppName = "msedge";
        h.Type("We went their today.\nI walked teh dog. ");
        h.Wait(2500);
        Assert.Equal("We went their today.\nI walked the dog. ", h.Text);   // the earlier line is left, the caret's line fixed
        Assert.Equal(0, h.Counters.Desyncs);
    }

    [Fact]
    public void R4_6_d_D121_TheLessonFadesAndIsShared()
    {
        var day = new DateOnly(2026, 11, 11);
        var health = new AppHealth(today: () => day);
        health.LineFixFailed("msedge");
        var shared = AppHealth.Combine([health.Local], day);
        Assert.True(shared.Apps["msedge"].LineFailures >= AppHealth.RetypeAfter);   // published on its own, like D89's
        day = day.AddDays(20);
        Assert.False(health.SameLineOnly("msedge"));                                 // about two weeks, then it's tried again
    }
}
