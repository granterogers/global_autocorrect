using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Tests.Instant;
using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D95, user report (Intercom, 2026-11-05, "Why not picking it up?"): the words from that email, through the
/// shipped dictionary and suggester.</summary>
public sealed class IntercomEmailTests
{
    private static readonly Lazy<SymSpellSuggester> Suggester = new(() => SymSpellSuggester.FromAssets(HunspellTests.Assets()));

    [Theory]
    [InlineData("tthis", "this")]
    [InlineData("descriiption", "description")]
    [InlineData("ino", null)]
    [InlineData("miial", null)]
    [InlineData("ofth", null)]
    [InlineData("thaskas", null)]
    public void R4_3_D95_WhatTheInstantLayerMakesOfTheEmailsWords(string typed, string? expected)
    {
        var c = new InstantCorrector(HunspellTests.Shared, Suggester.Value, _ => false);
        Assert.Equal(expected, c.TryCorrect(typed, isSentenceStart: false));
    }

    [Fact]
    public void R4_6_d_D95_WhenRetypingFailsTooTheProgramGoesBackToArrowKeys()
    {
        var h = new Harness(model: s => s.Replace("went their", "went there"), latencyMs: 50);
        var health = new GlobalAutocorrect.Core.Health.AppHealth();
        h.Engine.Health = health;
        h.Ed.AppName = "msedge";
        health.ArrowsFailed("msedge");      // learnt from one site's editor (D89)
        Assert.True(health.RetypeIn("msedge"));
        h.Ed.KeepsBackspacesUpTo = 10;     // and retyping goes wrong in this one, as in Intercom's emails

        h.Type("We went their yesterday and stayed all day. ");
        h.Wait(1500);
        Assert.Equal(1, h.Counters.Desyncs);
        Assert.False(health.RetypeIn("msedge"));   // retyping failed as often: arrows again

        h.Ed.Text = ""; h.Ed.Caret = 0;
        h.Engine.SetTarget(h.Ed);
        h.Type("They went their today and stayed all day. ");
        h.Wait(1500);
        Assert.Equal("They went there today and stayed all day. ", h.Text);   // arrows: no long Backspace burst
        Assert.Equal(1, h.Counters.Desyncs);
    }
}
