using GlobalAutocorrect.Core.Health;
using GlobalAutocorrect.Core.Tests.Simulated;
using GlobalAutocorrect.Core.Write;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Health;

/// <summary>D84, user request "if it happens once, it never happens again": the app learns from its own mistakes.</summary>
public sealed class MistakeMemoryTests
{
    private static Harness WithMemory(out MistakeMemory memory, out List<string> learnt)
    {
        var h = new Harness();
        memory = new MistakeMemory();
        h.Engine.Mistakes = memory;
        var list = new List<string>();
        h.Engine.LearntFromMistake += list.Add;
        learnt = list;
        return h;
    }

    [Fact]
    public void R4_8_D84_AFixTypedBackOverIsAMistake_AndIsNeverMadeAgain()
    {
        var h = WithMemory(out var memory, out var learnt);
        h.Type("I saw teh ");
        Assert.Equal("I saw the ", h.Text);
        // The user meant "teh" (a name, say): four Backspaces and they type it again.
        for (int i = 0; i < 4; i++) h.Engine.OnInput(h.Ed.Backspace());
        h.Type("teh ");
        Assert.Equal("I saw teh ", h.Text);             // not "fixed" a second time
        Assert.Contains("typed-back", learnt);
        Assert.True(memory.IsKnown("teh", "the"));
        Assert.Contains("teh", h.Personal);              // as an undo does (4.8.c)

        h.Type("and teh ");                              // later, the same word again
        Assert.Equal("I saw teh and teh ", h.Text);
    }

    // D185, user: "if I remove your correction and type the same thing again, that's my way of saying I don't want that
    // word corrected". Deleting the fix the usual ways restarts the typing session, which D84's match didn't survive.

    [Fact]
    public void R4_8_D185_AFixDeletedWithCtrlBackspaceAndTypedBackIsLearnt()
    {
        var h = WithMemory(out var memory, out var learnt);
        h.Type("Please send teh ");
        Assert.Equal("Please send the ", h.Text);
        h.Engine.OnInput(h.Ed.DeleteWordBack());
        Assert.Equal("Please send ", h.Text);
        h.Type("teh ");
        Assert.Equal("Please send teh ", h.Text);       // left as typed
        Assert.Contains("typed-back", learnt);
        Assert.True(memory.IsKnown("teh", "the"));
        Assert.Contains("teh", h.Personal);
    }

    [Fact]
    public void R4_8_D185_AFixSelectedAndDeletedThenTypedBackIsLearnt()
    {
        var h = WithMemory(out var memory, out var learnt);
        h.Type("Please send teh ");
        var (select, delete) = h.Ed.SelectBackAndDelete(4);
        h.Engine.OnInput(select);
        h.Engine.OnInput(delete);
        h.Type("teh ");
        Assert.Equal("Please send teh ", h.Text);
        Assert.Contains("typed-back", learnt);
        Assert.True(memory.IsKnown("teh", "the"));
    }

    [Fact]
    public void R4_8_D185_TheSameTypoTypedAgainElsewhereIsFixedAndNotLearnt()
    {
        var h = WithMemory(out var memory, out var learnt);
        h.Type("Please send teh report and teh invoice ");
        Assert.Equal("Please send the report and the invoice ", h.Text);   // the second is a typo too, not a "no"
        Assert.DoesNotContain("typed-back", learnt);
        Assert.False(memory.IsKnown("teh", "the"));
        Assert.DoesNotContain("teh", h.Personal);
    }

    [Fact]
    public void R4_8_D185_TypedBackLongAfterTheFixIsFixedAgain()
    {
        var h = WithMemory(out _, out var learnt);
        h.Type("Please send teh ");
        h.Engine.OnInput(h.Ed.DeleteWordBack());
        h.Clock.Advance(301_000);                       // past Learn.TypedBackWithinSeconds (300)
        h.Type("teh ");
        Assert.Equal("Please send the ", h.Text);
        Assert.DoesNotContain("typed-back", learnt);
    }

    [Fact]
    public void R4_8_D84_AnUndoneFixIsRemembered()
    {
        var h = WithMemory(out var memory, out var learnt);
        h.Type("I saw teh ");
        Assert.Equal(Undo.UndoResult.Reverted, h.Engine.Undo(global: false));
        Assert.Contains("undone", learnt);
        Assert.True(memory.IsKnown("teh", "the"));
    }

    [Fact]
    public void R4_6_D84_AFixThatLandsInCapitalsIsTypedAgainStraightAway()
    {
        var h = WithMemory(out _, out var learnt);
        h.Type("I saw");
        h.Ed.UppercaseNextEdit = true;                   // Shift or Caps Lock catches the app's keys
        h.Type(" teh ");
        Assert.Equal("I saw the ", h.Text);
        Assert.Contains("case-repaired", learnt);
        Assert.Equal(0, h.Counters.Desyncs);
    }

    [Theory]
    [InlineData("teh", "THE", "capitals")]               // capitals from nowhere
    [InlineData("the meeting", "THE MEETING", "capitals")]
    [InlineData("see you", "see you you", "doubled-word")]
    [InlineData("adaptor?", "adapter?", "spelling-variant")]   // D135: by any layer
    [InlineData("colour", "color", "spelling-variant")]
    [InlineData("hello", "hel￼lo", "odd-character")]
    public void R4_5_D84_TheWriteGuardRefusesChangesNoFixNeeds(string before, string after, string why) =>
        Assert.Equal(why, WriteGuard.Problem(before, after));

    [Theory]
    [InlineData("teh", "the")]
    [InlineData("i", "I")]
    [InlineData("the", "The")]                           // a sentence start
    [InlineData("THe", "The")]                           // D78
    [InlineData("i think monday", "I think Monday")]
    [InlineData("SHOUTING teh", "SHOUTING THE")]         // writing in capitals already
    [InlineData("I had had enough", "I had had enough")]
    [InlineData("untill", "until")]                      // D135: a typo, not a spelling
    public void R4_5_D84_TheWriteGuardLetsOrdinaryFixesThrough(string before, string after) =>
        Assert.Null(WriteGuard.Problem(before, after));

    [Fact]
    public void R8_6_D84_FingerprintsHoldNoWords_AndAreSharedOnlyWhenTwoPcsAgree()
    {
        string f = MistakeMemory.Fingerprint("teh", "the");
        Assert.Equal(16, f.Length);
        Assert.DoesNotContain("teh", f);
        Assert.Equal(f, MistakeMemory.Fingerprint(" TEH", "The "));   // case and spaces don't matter

        var a = new Dictionary<string, int> { [f] = 1, ["aaaa"] = 1 };
        var b = new Dictionary<string, int> { [f] = 1, ["bbbb"] = 2 };   // one PC, twice
        var shared = MistakeMemory.Combine([a, b]);
        Assert.Contains(f, shared.Mistakes.Keys);              // two PCs
        Assert.Contains("bbbb", shared.Mistakes.Keys);         // one PC, twice
        Assert.DoesNotContain("aaaa", shared.Mistakes.Keys);   // one PC, once: one person's taste
    }
}
