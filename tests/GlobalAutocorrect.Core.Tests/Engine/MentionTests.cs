using GlobalAutocorrect.Core.Tests.Simulated;
using GlobalAutocorrect.Core.Text;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D90, user report (Intercom, 2026-11-05): "@derekrees" was removed. An @mention tags someone: never changed,
/// and never stepped over or retyped, because editors turn it into one block that a single key covers.</summary>
public sealed class MentionTests
{
    [Theory]
    [InlineData("@derekrees", "@derekrees")]
    [InlineData("@derek.rees,", "@derek.rees")]
    [InlineData("(@derek_rees)", "@derek_rees")]
    public void R4_5_d_D90_AMentionIsOneProtectedToken(string text, string mention)
    {
        var t = Assert.Single(Tokeniser.Tokenise(text), t => t.IsWord);
        Assert.Equal(mention, t.Text);
        Assert.True(t.IsMention);
        Assert.True(t.IsProtected);
        Assert.False(Assert.Single(Tokeniser.Tokenise("sam@example.com"), t => t.IsWord).IsMention);   // an email is not a mention
    }

    [Theory]
    [InlineData("@derek rees")]   // split: even spacing changes a mention
    [InlineData("")]              // dropped
    [InlineData("@DerekRees")]    // re-cased
    public void R4_5_d_D90_TheModelCannotChangeOrRemoveAMention(string becomes)
    {
        var h = new Harness(model: s => s.Replace("@derekrees", becomes).Replace("went their", "went there"), latencyMs: 50);
        h.Type("@derekrees we went their today. ");
        h.Wait(1500);
        Assert.StartsWith("@derekrees ", h.Text);
    }

    [Fact]
    public void R4_6_D90_AFixBeforeAMentionIsNotWritten_ItWouldStepOverTheTag()
    {
        var h = new Harness(model: s => s.Replace("went their", "went there"), latencyMs: 50);
        h.Type("We went their with @derekrees today. ");
        h.Wait(1500);
        Assert.Equal("We went their with @derekrees today. ", h.Text);
        Assert.Equal("mention-in-way", h.Engine.LastDropReason);
        Assert.Equal(0, h.Counters.Desyncs);
    }

    [Fact]
    public void R4_6_D90_AFixAfterAMentionIsStillWritten()
    {
        var h = new Harness(model: s => s.Replace("went their", "went there"), latencyMs: 50);
        h.Type("@derekrees we went their today. ");
        h.Wait(1500);
        Assert.Equal("@derekrees we went there today. ", h.Text);
    }

    [Fact]
    public void R4_6_D90_AnInstantFixAfterAMentionIsStillWritten()
    {
        var h = new Harness();
        h.Type("@derekrees I saw teh ");
        h.Wait(300);
        Assert.Equal("@derekrees I saw the ", h.Text);
    }
}
