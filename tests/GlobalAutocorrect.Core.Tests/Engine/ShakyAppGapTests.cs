using GlobalAutocorrect.Core.Health;
using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D98, user report (Intercom note, 2026-11-05, "didn't catch that"): where a fix stepped to with arrow keys has
/// gone wrong, a fix far from the caret waits for a real pause instead of landing between two keys.</summary>
public sealed class ShakyAppGapTests
{
    private static Harness TypedThenTypingOn(bool shaky)
    {
        // The answer arrives while the user is typing the next sentence: far from the caret, so arrows (or a long retype).
        var h = new Harness(model: s => s.Replace("went their", "went there"), latencyMs: 1200);
        var health = new AppHealth();
        h.Engine.Health = health;
        h.Ed.AppName = "msedge";
        if (shaky) health.ArrowsFailed("msedge");
        h.Type("We went their yesterday and stayed all day. ");
        h.Wait(300);
        h.Type("Then we came home and slept", msPerKey: 200);   // typing on, with 200 ms between keys
        return h;
    }

    [Fact]
    public void R4_6_c_D98_InAShakyProgramAFarFixWaitsForARealPause()
    {
        var h = TypedThenTypingOn(shaky: true);
        Assert.Contains("went their", h.Text);         // never between two keys
        h.Wait(1000);
        Assert.Equal("We went there yesterday and stayed all day. Then we came home and slept", h.Text);
        Assert.Equal(0, h.Counters.Desyncs);
    }

    [Fact]
    public void R4_6_c_D98_ElsewhereItStillLandsInAShortGap()
    {
        var h = TypedThenTypingOn(shaky: false);
        Assert.Contains("went there", h.Text);
    }
}
