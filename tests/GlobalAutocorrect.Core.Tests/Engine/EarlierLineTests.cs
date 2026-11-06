using GlobalAutocorrect.Core.Health;
using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D101, user report (Notepad, Intercom, 2026-11-06): "if I type a sentence, then press a new line, it seems to
/// just stop you from auto-correcting ... keep in memory the previous lines until such times as they've been corrected".</summary>
public sealed class EarlierLineTests
{
    [Fact]
    public void R4_4_a_D101_ALineFinishedWhileTheAiWasBusyIsStillCorrected()
    {
        // The "Fine. " request is still out when Enter ends the line, and the user types straight on.
        var h = new Harness(model: s => s.Replace("went their", "went there"), latencyMs: 2000);
        h.Type("Fine. We went their today\nNext line here and more");
        h.Wait(5000);
        Assert.Equal("Fine. We went there today\nNext line here and more", h.Text);
    }

    [Fact]
    public void R4_6_c_D101_AProgramThatLearntToRetypeStillFixesAnEarlierLine()
    {
        var h = new Harness(model: s => s.Replace("went their", "went there"), latencyMs: 50);
        var health = new AppHealth();
        h.Engine.Health = health;
        h.Ed.AppName = "Notepad";
        health.ArrowsFailed("Notepad");           // retypes from now on (D89)
        Assert.True(health.RetypeIn("Notepad"));
        h.Type("We went their today\n");
        h.Ed.ModifierHeld = false;
        h.Wait(1500);
        Assert.Equal("We went there today\n", h.Text);
        Assert.Equal(0, h.Counters.Desyncs);
    }
}
