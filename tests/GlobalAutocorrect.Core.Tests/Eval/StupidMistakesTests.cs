using GlobalAutocorrect.Core.Eval;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Eval;

/// <summary>D132: the kinds of mistake the real-use gate allows none of, from the user's own reports.</summary>
public sealed class StupidMistakesTests
{
    private static IEnumerable<string> Kinds(string noisy, string clean, string output, params string[] keep) =>
        StupidMistakes.Find(noisy, clean, output, keep).Select(m => m.Kind);

    [Fact]
    public void D132_AGoodFixHasNone() =>
        Assert.Empty(Kinds("sorry I issed your call", "sorry I missed your call", "sorry I missed your call"));

    [Fact]
    public void D132_AnAddedQuestionMark() =>   // WhatsApp, "(or later)? at Portillo?"
        Assert.Contains("added-?!", Kinds("at 1630 (or later) at Portillo?", "at 1630 (or later) at Portillo?", "at 1630 (or later)? at Portillo?"));

    [Fact]
    public void D132_CapitalsNobodyTyped() =>   // Intercom, "WE RECOMMEND YOU TO MIGRATE"
        Assert.Contains("capitals", Kinds("we recomend you to migrate", "we recommend you to migrate", "WE RECOMMEND you to migrate"));

    [Fact]
    public void D132_TypedCapitalsAreFine() =>
        Assert.Empty(Kinds("the OEM licence is fine", "the OEM licence is fine", "the OEM licence is fine"));

    [Fact]
    public void D132_ARepeatedWord() =>   // "slimmed-down -downversion", "uuupdate"
        Assert.Contains("repeated-word", Kinds("the slimmed down version", "the slimmed down version", "the slimmed down down version"));

    [Fact]
    public void D132_ACorrectWordChanged() =>
        Assert.Contains("correct-word-changed", Kinds("Thanks for the update", "Thanks for the update", "Thanks for the upgrade"));

    [Fact]
    public void D132_ANameOrTimeTouched() =>
        Assert.Contains("protected-changed", Kinds("see you at Portillo at 1630", "see you at Portillo at 1630", "see you at Portillo at 16:30", "1630"));

    [Fact]
    public void D132_AWordLost() =>
        Assert.Contains("word-lost", Kinds("we can meet tomorrow morning", "we can meet tomorrow morning", "we can meet morning"));
}
