using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Tests.EvalData;
using GlobalAutocorrect.Core.Text;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>
/// D173, user report ("that would be apprectea d.77", never fixed, though the model wrote "appreciated."): a stray number
/// no longer throws away the whole correction, and non-word pieces may be joined into one word.
/// </summary>
[Collection("Tunables")]
public sealed class StrayNumberAndJoinTests
{
    private static readonly EditRules Rules = new(new EditRuleSettings(),
        HunspellSpellChecker.FromAssets(Path.Combine(EvalDataFiles.RepoRoot(), "assets")), _ => false);

    private static string Apply(string a, string b) => TextEdit.ApplyAll(a, Rules.Filter(a, b, WordDiff.Compute(a, b)).Accepted);

    private const string Meant = "If you can kindly give me an alternate, that would be appreciated.";

    [Fact]
    public void R4_5_d_D173_TheUsersSentenceIsFixedAndTheStrayNumberKept() =>
        Assert.Equal("If you can kindly give me an alternate, that would be appreciated.77",
            Apply("If you can kindly give me an alternate, that would be apprectea d.77", Meant));

    [Fact]
    public void R4_5_a_D173_NonWordPiecesAreJoinedIntoOneWord() =>
        Assert.Equal(Meant, Apply("If you can kindly give me an alternate, that would be apprectea d.", Meant));

    [Theory]
    [InlineData("the qz xv end", "the quiz end")]                                     // non-words, but far apart
    public void R4_5_a_D173_OnlyCloseNonWordPiecesAreJoined(string typed, string model) => Assert.Equal(typed, Apply(typed, model));

    [Fact]
    public void R4_5_d_D173_ANumberIsStillNeverChangedOrDropped()
    {
        Assert.Equal("meet the team at 3pm", Apply("meet teh team at 3pm", "meet the team at 4pm"));
        Assert.Equal("send the order to me, room 12", Apply("send teh order to me, room 12", "send the order to me, room"));
    }
}
