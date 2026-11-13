using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Tests.Simulated;
using GlobalAutocorrect.Core.Write;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D127, chaos typing on GitHub: "he" fixed to "the" came out "tthe" or "tttthe" in every editor.</summary>
public sealed class LateAnswerTests
{
    [Fact]
    public void R4_6_d_D127_ALateAnswerForAWordAlreadyFixedIsNotWrittenAgain()
    {
        var h = new Harness();
        h.Type("I saw he dog today. ");
        h.Wait(500);
        int at = h.Text.IndexOf(" he ", StringComparison.Ordinal) + 1;
        long gen = h.Engine.Session.Generation;
        h.Engine.EnqueueForTest(new PendingWrite(Layer.Ai, gen, at, at + 2, "he", "the", h.Clock.NowMs, h.Ed.LatestKeySeq, ["he"]));
        h.Wait(2000);
        Assert.Equal("I saw the dog today. ", h.Text);
        // The second answer, computed before the first fix and moved along by it: "he" still matches inside "the".
        h.Engine.EnqueueForTest(new PendingWrite(Layer.Ai, h.Engine.Session.Generation, at + 1, at + 3, "he", "the", h.Clock.NowMs, h.Ed.LatestKeySeq, ["he"]));
        h.Wait(2000);
        Assert.Equal("I saw the dog today. ", h.Text);
    }

    [Fact]
    public void R4_6_d_D127_ALateInsertedLetterIsNotWrittenTwice()
    {
        var h = new Harness();
        h.Type("I saw he dog today. ");
        h.Wait(500);
        int at = h.Text.IndexOf(" he ", StringComparison.Ordinal) + 1;
        // As the GitHub run logged it: insert "t" before "he" (the changed letters only), twice.
        h.Engine.EnqueueForTest(new PendingWrite(Layer.Ai, h.Engine.Session.Generation, at, at, "", "t", h.Clock.NowMs, h.Ed.LatestKeySeq, ["he"]));
        h.Wait(2000);
        h.Engine.EnqueueForTest(new PendingWrite(Layer.Ai, h.Engine.Session.Generation, at + 1, at + 1, "", "t", h.Clock.NowMs, h.Ed.LatestKeySeq, ["he"]));
        h.Wait(2000);
        Assert.Equal("I saw the dog today. ", h.Text);
    }
}
