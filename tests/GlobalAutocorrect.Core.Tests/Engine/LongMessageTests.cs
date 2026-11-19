using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D146, stress run: past 400 characters the field read looked like a different field and fixes were thrown away.</summary>
public sealed class LongMessageTests
{
    [Fact]
    public void R4_6_c_D146_AMessageLongerThanTheReadIsStillFixed()
    {
        var h = new Harness(model: s => s.Replace("ofr the", "for the"), latencyMs: 300);
        string filler = string.Concat(Enumerable.Repeat("We spoke about the plan and agreed the next steps together. ", 7));   // 420 characters
        Assert.True(filler.Length > 400);
        h.Type(filler + "Thanks ofr the update. ");
        h.Wait(4000);
        Assert.EndsWith("Thanks for the update. ", h.Text);
        Assert.Equal(0, h.Counters.FieldMismatches);   // the read limit isn't a different field
        Assert.Equal(0, h.Counters.Desyncs);
    }

    [Fact]
    public void R4_6_c_D146_PastTheReadAndJustAfterEnterItIsStillTheSameField()
    {
        var h = new Harness(model: s => s.Replace("teh deadline", "the deadline"), latencyMs: 300);
        string filler = string.Concat(Enumerable.Repeat("We spoke about the plan and agreed the next steps together. ", 7));
        h.Type(filler + "\nteh deadline moved. ");
        h.Wait(4000);
        Assert.EndsWith("\nthe deadline moved. ", h.Text);
        Assert.Equal(0, h.Counters.FieldMismatches);
    }
}
