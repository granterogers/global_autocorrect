using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>
/// D118, user reports (Intercom email: "Best Wishes,w", "update. y", "uuupdate"): after a fix on an earlier line the caret
/// stopped at the end of that line, and the next letter went there.
/// </summary>
public sealed class LineEndReturnTests
{
    [Fact]
    public void R4_6_d_D118_AfterAFixOnAnEarlierLineTheNextLettersGoWhereTheUserIs()
    {
        var h = new Harness(model: s => s.Replace("anu update", "any update"), latencyMs: 50);
        h.Ed.RightStopsAtLineEnd = true;
        h.Type("Thanks for anu update.\n\n");
        h.Wait(1700);                      // a pause: the fix on the earlier line goes in
        h.Type("Best wishes,");
        h.Wait(1700);
        Assert.Equal("Thanks for any update.\n\nBest wishes,", h.Text);
        Assert.Equal(h.Text.Length, h.Ed.Caret);
        Assert.True(h.Ed.EditsThenEnd >= 1);
        Assert.Equal(0, h.Counters.Desyncs);
    }

    [Fact]
    public void R4_6_d_D118_AFixOnTheCaretsOwnLineStillStepsBackWithRightPresses()
    {
        var h = new Harness(model: s => s.Replace("went their", "went there"), latencyMs: 50);
        h.Type("We went their today and had lunch. ");
        h.Wait(1700);
        Assert.Equal("We went there today and had lunch. ", h.Text);
        Assert.Equal(0, h.Ed.EditsThenEnd);
    }
}
