using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D91, user report (the Claude app, 2026-11-05): after Enter sent a message and the box emptied, an AI fix still
/// queued for it could never be matched to the empty field; the field was given up on as stuck, and the next message,
/// typed into the same box, was not corrected at all.</summary>
public sealed class SentMessageTests
{
    [Fact]
    public void R4_6_d_D91_AfterEnterSendsAndTheBoxEmpties_TheNextMessageIsStillCorrected()
    {
        var h = new Harness(model: s => s.Replace("went their", "went there"), latencyMs: 300);
        string? stuck = null;
        h.Engine.Stuck += app => stuck = app;
        h.Type("We went their today.\n");
        h.Ed.Text = "";      // the app sent the message and cleared the box
        h.Ed.Caret = 0;
        h.Wait(3000);        // the AI's fix arrives for a message that is gone

        Assert.Null(stuck);
        Assert.Equal("", h.Text);                            // nothing written into the empty box
        h.Type("I saw teh ");
        h.Wait(300);
        Assert.Equal("I saw the ", h.Text);
    }

    [Fact]
    public void R4_6_d_D91_AnEmptyReadWithoutEnterIsStillALaggingRead()
    {
        var s = new GlobalAutocorrect.Core.Session.TypingSession();
        foreach (char c in "hello") s.Apply(new GlobalAutocorrect.Core.Session.KeyInput(GlobalAutocorrect.Core.Session.KeyKind.Char, c, 1, 0));
        Assert.False(s.Resync(""));
        Assert.Equal("hello", s.Typed);
    }
}
