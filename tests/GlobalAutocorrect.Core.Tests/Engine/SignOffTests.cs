using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D110, user report (Intercom reply, "what happened?"): "Thakms," at the end of a reply was never corrected.</summary>
public sealed class SignOffTests
{
    private static string Model(string s) => s.Replace("Thakms", "Thanks");

    [Theory]
    [InlineData("Hi Jose,\n\nI have tried without success to call your office. Can you kindly provide me a direct line?\n\nThakms,")]
    [InlineData("Hi Jose,\n\nI have tried without success to call your office. Can you kindly provide me a direct line?\n\nThakms,\n")]
    public void R4_4_a_D110_ASignOffTypedLastIsCorrected(string text)
    {
        var h = new Harness(model: Model, latencyMs: 400);
        h.Type(text);
        h.Wait(4000);
        Assert.Contains("Thanks,", h.Text);
        Assert.Equal(0, h.Counters.Desyncs);
    }
}
