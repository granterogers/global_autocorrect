using GlobalAutocorrect.Core.Health;
using GlobalAutocorrect.Core.Targets;
using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>
/// D122, user request "I don't want any trade-off" (after D121 left earlier-line typos unfixed in Intercom): a fix on an
/// earlier line is written by putting the caret on it through UI Automation, so no arrow keys cross a line break.
/// </summary>
public sealed class PlacedFixTests
{
    private static Harness Make(AppHealth health)
    {
        var h = new Harness(model: s => s.Replace("went their", "went there").Replace("teh dog", "the dog"), latencyMs: 50);
        h.Engine.Health = health;
        h.Ed.AppName = "msedge";
        h.Ed.CanPlaceCaret = true;
        return h;
    }

    [Fact]
    public void R4_6_d_D122_AnEarlierLineIsFixedByPlacingTheCaretEvenWhereArrowsWentWrong()
    {
        var health = new AppHealth();
        health.LineFixFailed("msedge");   // D121's lesson: arrows across a line break went wrong here
        var h = Make(health);
        h.Type("We went their today.\nI walked teh dog. ");
        h.Wait(3000);
        Assert.Equal("We went there today.\nI walked the dog. ", h.Text);
        Assert.Equal(h.Text.Length, h.Ed.Caret);       // back where the user was
        Assert.True(h.Ed.PlacedEdits >= 1);
        Assert.Equal(0, h.Counters.Desyncs);
    }

    [Fact]
    public void R4_6_d_D122_WhenTheFieldWontShowTheFixNothingIsTyped()
    {
        var health = new AppHealth();
        var h = Make(health);
        h.Ed.PlaceRefuses = true;
        h.Type("We went their today.\nI walked teh dog. ");
        h.Wait(3000);
        Assert.Equal("We went their today.\nI walked the dog. ", h.Text);   // a miss, never damage
        Assert.Equal(0, h.Counters.Desyncs);
    }

    [Fact]
    public void R4_6_d_D122_WherePlacingWentWrongEarlierLinesAreLeft()
    {
        var health = new AppHealth();
        health.LineFixFailed("msedge");
        health.PlaceFailed("msedge");
        var h = Make(health);
        h.Type("We went their today.\nI walked teh dog. ");
        h.Wait(3000);
        Assert.Equal("We went their today.\nI walked the dog. ", h.Text);
        Assert.Equal(0, h.Ed.PlacedEdits);
    }

    [Fact]
    public void R4_6_d_D122_TheFieldsWayOfShowingTheTailIsFound()
    {
        // Typed: "their" then " today.\n\nI walked" — the field shows the blank line as one line break (D42).
        var tails = TextNorm.FieldTails("We went their today.\nI walked", "their", " today.\n\nI walked");
        Assert.Contains(" today.\nI walked", tails);
        Assert.Empty(TextNorm.FieldTails("We went there today.\nI walked", "their", " today.\nI walked"));   // not what's there
    }
}
