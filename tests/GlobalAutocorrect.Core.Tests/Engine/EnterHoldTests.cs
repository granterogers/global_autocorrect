using GlobalAutocorrect.Core.Health;
using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D154 (chat: the AI's fixes arrived after Enter had sent the message): Enter waits, briefly, for the line's fixes.</summary>
public sealed class EnterHoldTests
{
    private static void Send(Harness h)
    {
        h.Engine.OnInput(h.Ed.TypeChar('\n'));
        h.Ed.Text = "";   // the chat box sends the message and empties
        h.Ed.Caret = 0;
        h.Wait(500);
    }

    [Fact]
    public void R4_6_D154_AFieldThatEmptiesAfterEnterTwiceSendsOnEnter()
    {
        var h = new Harness();
        h.Ed.AppName = "chat";
        h.Engine.Health = new AppHealth();
        h.Type("See you at five");
        Send(h);
        Assert.False(h.Engine.Health.EnterSends("chat"));   // once could be anything
        h.Type("Bring the notes");
        Send(h);
        Assert.True(h.Engine.Health.EnterSends("chat"));
    }

    [Fact]
    public void R4_6_D154_AnOrdinaryLineBreakIsNotASend()
    {
        var h = new Harness();
        h.Ed.AppName = "notes";
        h.Engine.Health = new AppHealth();
        h.Type("First line\nSecond line\nThird line\n");
        h.Wait(500);
        Assert.False(h.Engine.Health.EnterSends("notes"));
    }

    [Fact]
    public void R4_6_D156_AnInstantFixWaitingToBeWrittenIsReported()
    {
        // Stress, TinyMCE: "provkded." and Enter straight after; the fix was placed across the new paragraph and the break lost.
        var h = new Harness();
        h.Ed.BlockFencedEdits = 1;   // the next key was already on its way: the first try is refused, the fix waits
        foreach (char c in "I saw teh ") { h.Clock.Advance(20); h.Engine.OnInput(h.Ed.TypeChar(c)); }
        Assert.True(h.Engine.InstantFixPending, $"queued={h.Engine.QueuedWrites} text=[{h.Text}] drop={h.Engine.LastDropReason}");    // Enter now would wait for it
        h.Wait(300);
        Assert.False(h.Engine.InstantFixPending);
        Assert.Equal("I saw the ", h.Text);
    }

    [Fact]
    public void R4_6_D156_TheWordEnterWouldFinishIsFixedBeforeTheEnterGoes()
    {
        // Stress, TinyMCE: "provkded." then Enter; Enter itself finished the word, so the fix was placed across the new
        // line and the break was lost ("provided.Dear"). Now the word is fixed while Enter waits, then Enter goes.
        var h = new Harness();
        foreach (char c in "I saw teh.") { h.Clock.Advance(40); h.Engine.OnInput(h.Ed.TypeChar(c)); }
        Assert.True(h.Engine.WordAtEndWouldBeFixed());   // so Enter is held
        Assert.True(h.Engine.FinishWordForEnter());       // the held Enter's word, fixed on its own line
        h.Wait(200);
        Assert.Equal("I saw the.", h.Text);
        h.Type("\nNext line ");                           // the Enter let go, and typing on
        h.Wait(300);
        Assert.Equal("I saw the.\nNext line ", h.Text);   // the break is there, the word fixed once
    }

    [Fact]
    public void R4_6_D157_AnInstantFixIsNeverWrittenAcrossALineBreak()
    {
        // E2E, CKEditor: the fix couldn't be placed before Enter, landed after it, and the next line came out "HIi".
        var h = new Harness();
        foreach (char c in "I saw teh\n") { h.Clock.Advance(40); h.Engine.OnInput(h.Ed.TypeChar(c)); }   // Enter not held
        h.Wait(300);
        Assert.Equal("I saw teh\n", h.Text);   // left as typed: a miss, never damage
        Assert.Equal("instant-across-line", h.Engine.LastDropReason);
    }

    [Fact]
    public void R4_6_D156_AWordThatNeedsNoFixDoesNotHoldEnter()
    {
        var h = new Harness();
        foreach (char c in "I saw the.") { h.Clock.Advance(40); h.Engine.OnInput(h.Ed.TypeChar(c)); }
        Assert.False(h.Engine.WordAtEndWouldBeFixed());
        Assert.False(h.Engine.FinishWordForEnter());
    }

    [Fact]
    public void R4_6_D154_HeldEnterChecksTheLineNowAndSaysWhenItsFixesAreIn()
    {
        var h = new Harness(model: s => s.Replace("by th harbour", "by the harbour"), latencyMs: 300);
        h.Type("Fish and chips by th harbour", msPerKey: 40);
        Assert.False(h.Engine.LineFixesPending);   // no pause yet: nothing asked
        h.Engine.CheckLineNow();                    // Enter held: ask straight away
        Assert.True(h.Engine.LineFixesPending);
        for (int i = 0; i < 80 && h.Engine.LineFixesPending; i++) h.Wait(25);
        Assert.False(h.Engine.LineFixesPending);
        Assert.Equal("Fish and chips by the harbour", h.Text);
    }
}
