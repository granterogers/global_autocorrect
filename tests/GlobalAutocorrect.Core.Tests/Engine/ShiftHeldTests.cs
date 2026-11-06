using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D100, user report (Intercom, 2026-11-06, "why all caps? stop doing that"): a word fixed while Shift was down for
/// the next word's capital came out in capitals.</summary>
public sealed class ShiftHeldTests
{
    [Fact]
    public void R4_6_c_D100_AnInstantFixWaitsWhileShiftIsHeld()
    {
        var h = new Harness(model: s => s, latencyMs: 50);
        h.Type("I saw teh");
        h.Engine.OnInput(h.Ed.TypeChar(' '));
        h.Ed.ModifierHeld = true;          // straight after the space, Shift down for the next word's capital
        h.Wait(400);
        Assert.Equal("I saw teh ", h.Text);
        h.Ed.ModifierHeld = false;
        h.Wait(300);
        Assert.Equal("I saw the ", h.Text);
    }
}
