using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D105: a fix the hook swallowed (a key got to the app first) changed nothing, so it is simply tried again.</summary>
public sealed class WriteFenceGateTests
{
    [Fact]
    public void R4_6_c_D105_ASwallowedFixIsTriedAgainAndLandsWithNoDesync()
    {
        var h = new Harness(model: s => s.Replace("went their", "went there"), latencyMs: 50);
        h.Ed.BlockFencedEdits = 2;
        h.Type("We went their yesterday and stayed all day. ");
        h.Wait(2000);
        Assert.Equal("We went there yesterday and stayed all day. ", h.Text);
        Assert.Equal(0, h.Ed.BlockFencedEdits);
        Assert.Equal(0, h.Counters.Desyncs);
    }
}
