using GlobalAutocorrect.Core.Prediction;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Prediction;

/// <summary>D142: phrases the user repeats are anticipated; nothing typed once is ever kept.</summary>
public sealed class PhraseMemoryTests
{
    private static void Say(PhraseMemory m, string sentence)
    {
        foreach (var w in sentence.Split(' ')) m.Word(w);
        m.Break();
    }

    private static (PhraseMemory M, Action NextDay) Make()
    {
        var day = new DateOnly(2026, 11, 19);
        var m = new PhraseMemory(() => day);
        return (m, () => day = day.AddDays(1));
    }

    [Fact]
    public void R7_D142_APhraseRepeatedOnTwoDaysIsAnticipated()
    {
        var (m, nextDay) = Make();
        Say(m, "let me know if you have any questions");
        Say(m, "let me know if you have any questions");
        Assert.Null(m.Continue("let me know "));      // twice, one day: nothing kept yet
        nextDay();
        Say(m, "let me know if you have any questions");
        Assert.Equal("if you have any questions", m.Continue("Please let me know "));
        Assert.Equal("ow if you have any questions", m.Continue("let me kn"));   // finishing the word being typed
    }

    [Fact]
    public void R7_D142_ASentenceTypedOnceIsNeverKept()
    {
        var (m, nextDay) = Make();
        Say(m, "the customer said their invoice was wrong");
        nextDay();
        Say(m, "the customer said their invoice was wrong");   // twice, even on two days: still not 3 times
        Assert.Empty(m.Kept);
        Assert.Null(m.Continue("the customer said "));
    }

    [Fact]
    public void R7_D142_WhenWhatFollowedDisagreesOnlyTheAgreedPartIsOffered()
    {
        var (m, nextDay) = Make();
        for (int d = 0; d < 3; d++)   // both kept: 6 uses against 3
        {
            Say(m, "let me know if you have any questions");
            Say(m, "let me know if you have any questions");
            Say(m, "let me know if we can help further");
            nextDay();
        }
        // "if" always follows; then "you have any questions" 6 times in 9 (67%, under 70%): only "if", too short: nothing.
        Assert.Null(m.Continue("let me know "));
    }

    [Fact]
    public void R7_D142_KeptPhrasesSurviveASaveEncryptedByTheAppAndForgetWorks()
    {
        var (m, nextDay) = Make();
        for (int d = 0; d < 2; d++) { Say(m, "thanks for getting back to me"); Say(m, "thanks for getting back to me"); nextDay(); }
        Assert.Contains(m.Kept, k => k.Phrase == "thanks for getting back to me");
        var (again, _) = Make();
        again.Load(m.Save());
        Assert.Equal("getting back to me", again.Continue("Thanks for "));
        again.Forget("getting back");
        Assert.Null(again.Continue("Thanks for "));
    }

    [Fact]
    public void R7_D142_OffMeansNothingLearnedOrOffered()
    {
        var (m, nextDay) = Make();
        m.Enabled = false;
        for (int d = 0; d < 3; d++) { Say(m, "best wishes to you all"); Say(m, "best wishes to you all"); nextDay(); }
        Assert.Empty(m.Kept);
        Assert.Null(m.Continue("best wishes "));
    }
}
