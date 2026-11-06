using GlobalAutocorrect.Core.Targets;
using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D99, user report (Intercom email, 2026-11-06, "DWGfFile"): a fix landed but the editor lost some of the Right
/// presses after it, so the caret sat inside the text and the next keys went there.</summary>
public sealed class CaretShortTests
{
    [Theory]
    [InlineData("We went there yesterday", "We went there yest", "erday", 5)]
    [InlineData("We went there yesterday", "We went there yesterday", "", 0)]        // where it should be
    [InlineData("We went there yesterday", "We went their yest", "erday", 0)]        // the fix isn't there
    [InlineData("Hi\nWe went there", "Hi", "\nWe went there", 0)]                    // across a line: not guessed
    public void R4_6_d_D99_TellsHowFarShortTheCaretIs(string expected, string before, string after, int shortBy) =>
        Assert.Equal(shortBy, TextNorm.CaretShort(expected, before, after, 300));

    [Fact]
    public void R4_6_d_D99_TheCaretIsPutBackAndTypingGoesOnWhereTheUserWas()
    {
        var h = new Harness(model: s => s.Replace("went their", "went there"), latencyMs: 50);
        h.Ed.KeepsRightArrowsUpTo = 5;
        h.Type("We went their yesterday and stayed all day. ");
        h.Wait(1500);
        Assert.Equal(h.Text.Length, h.Ed.Caret);
        h.Type("More soon.");
        h.Wait(1500);
        Assert.Equal("We went there yesterday and stayed all day. More soon.", h.Text);
        Assert.Equal(0, h.Counters.Desyncs);
    }
}
