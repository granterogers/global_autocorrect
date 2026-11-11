using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D117: a fix far back or on an earlier line waits for a real pause, so no key lands while it is applied.</summary>
public sealed class LongFixPauseTests
{
    [Fact]
    public void R4_6_b_D117_AFixOnAnEarlierLineWaitsForAPauseThenLands()
    {
        var h = new Harness(model: s => s.Replace("went their", "went there"), latencyMs: 50);
        h.Type("We went their today.\nThen we carried on typing ");
        h.Wait(900);                                    // a short think: not long enough
        Assert.StartsWith("We went their", h.Text);
        h.Type("and more ");
        h.Wait(1700);                                   // a real pause
        Assert.StartsWith("We went there", h.Text);
        Assert.Equal(0, h.Counters.Desyncs);
    }

    [Fact]
    public void R4_6_b_D117_AFixCloseToTheCaretIsNotHeldBack()
    {
        var h = new Harness(model: s => s.Replace("went their", "went there"), latencyMs: 50);
        h.Type("We went their today. ");
        h.Wait(700);
        Assert.Equal("We went there today. ", h.Text);
    }
}
