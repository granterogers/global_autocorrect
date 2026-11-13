using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>
/// D124, user report (WhatsApp: "Hey, sorry im issed your call, I was out hiking this morning", nothing fixed): a line
/// break typed by mistake (Shift still down from the "I" when Enter went) and deleted again straight away.
/// </summary>
public sealed class LineBreakTakenBackTests
{
    [Fact]
    public void R4_4_D124_ALineBreakTypedAndDeletedAgainDoesNotStopTheMessageBeingFixed()
    {
        var h = new Harness(model: s => s.Replace("issed your", "missed your"), latencyMs: 50);
        h.Type("Hey, sorry I issed your call, I");
        h.Engine.OnInput(h.Ed.TypeChar('\n'));
        h.Wait(120);
        h.Engine.OnInput(h.Ed.Backspace());
        h.Wait(120);
        h.Type(" was out hiking this morning");
        h.Wait(4000);
        Assert.Equal("Hey, sorry I missed your call, I was out hiking this morning", h.Text);
    }

    [Fact]
    public void R4_4_D124_KeysTheAppNeverShowedDoNotStopFixesForever()
    {
        var h = new Harness(model: s => s.Replace("issed your", "missed your"), latencyMs: 50);
        h.Type("Hey, sorry I issed your call, I was out hiking this morning");
        h.Engine.OnInput(h.Ed.SwallowedChar('x'));   // the app used the last key for something else
        h.Engine.OnInput(h.Ed.SwallowedChar('y'));
        h.Wait(5000);
        Assert.Equal("Hey, sorry I missed your call, I was out hiking this morning", h.Text);
        Assert.Equal(0, h.Counters.Desyncs);
    }
}
