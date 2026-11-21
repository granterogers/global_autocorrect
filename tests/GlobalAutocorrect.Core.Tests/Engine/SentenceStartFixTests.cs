using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D141, stress runs: "fungers" at the start of a line was fixed to "fingers", the sentence's capital left out.</summary>
public sealed class SentenceStartFixTests
{
    [Fact]
    public void R4_3_D141_AnInstantFixAtASentenceStartCarriesTheCapital()
    {
        var h = new Harness(options: new EngineOptions());   // spacing and capitals rules on, as in the app
        h.Type("Thanks. teh dog is fine. ");
        h.Wait(300);
        Assert.StartsWith("Thanks. The dog", h.Text);
    }

    [Fact]
    public void R4_3_D141_TheFirstWordOfAnEmptyFieldIsASentenceStart()
    {
        var h = new Harness(options: new EngineOptions());
        h.Type("teh dog is fine. ");
        h.Wait(300);
        Assert.StartsWith("The dog", h.Text);
    }

    [Fact]
    public void R4_3_D149_AFieldAKeyBehindStillShowsTheSentenceStart()
    {
        // The chat box in the stress run: held keys reached the field a moment after the record had them.
        var h = new Harness(options: new EngineOptions());
        h.Type("teh");
        h.Ed.DistortBefore = s => s.EndsWith(' ') ? s[..^1] : s;   // the space isn't drawn yet
        h.Engine.OnInput(h.Ed.TypeChar(' '));
        h.Ed.DistortBefore = null;
        h.Type("dog is fine. ");
        h.Wait(300);
        Assert.StartsWith("The dog", h.Text);
    }

    [Fact]
    public void R4_3_D167_AnUnreadableFieldAtTheMomentOfTheFixStillGetsTheCapital()
    {
        // Stress, Quill: "fungers" fixed to "fingers" while the field couldn't be read; the capital is checked at write time.
        var h = new Harness(options: new EngineOptions());
        h.Type("teh");
        h.Ed.FailNextReads = 2;   // the sentence-start check and the first write attempt see nothing
        h.Engine.OnInput(h.Ed.TypeChar(' '));
        h.Wait(300);
        h.Type("dog is fine. ");
        h.Wait(300);
        Assert.StartsWith("The dog", h.Text);
    }

    [Fact]
    public void R4_3_D141_MidSentenceTheCaseIsKept()
    {
        var h = new Harness(options: new EngineOptions());
        h.Type("Thanks for teh dog. ");
        h.Wait(300);
        Assert.StartsWith("Thanks for the dog", h.Text);
    }
}
