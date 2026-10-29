using System.Diagnostics;
using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Session;
using GlobalAutocorrect.Core.Tests.Simulated;
using GlobalAutocorrect.Core.Undo;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

public sealed class EngineTests
{
    // ---- Instant layer (4.3) ------------------------------------------------------------------

    [Fact]
    public void R0_2_a_TehAndBookk()
    {
        var h = new Harness();
        h.Type("I read teh bookk ");
        Assert.Equal("I read the book ", h.Text);
    }

    [Fact]
    public void R4_3_a_FixesTheFinishedWordWithoutWaiting()
    {
        var h = new Harness();
        h.Type("we saw teh");
        h.Engine.OnInput(h.Ed.TypeChar(' '));   // no clock advance, no tick: the fix must already be there
        Assert.Equal("we saw the ", h.Text);
    }

    [Fact]
    public void R4_3_b_InstantLayerNeverCallsTheModel()
    {
        var h = new Harness(model: s => s);
        h.Type("we saw teh ");
        Assert.Equal("we saw the ", h.Text);
        Assert.Empty(h.Llm!.Requests);
    }

    [Theory]
    [InlineData("we met Smiht ")]      // capitalised mid-sentence
    [InlineData("code teh2 here ")]    // digits
    [InlineData("see www.teh.com ")]   // URL
    public void R4_3_c_LeavesExcludedWordsAlone(string typed)
    {
        var h = new Harness();
        h.Type(typed);
        Assert.Equal(typed, h.Text);
    }

    [Fact]
    public void R4_3_c_PersonalDictionaryWordsAreLeftAlone()
    {
        var h = new Harness();
        h.Personal.Add("teh");
        h.Type("we saw teh ");
        Assert.Equal("we saw teh ", h.Text);
    }

    [Fact]
    public void R4_3_d_UncertainWordsAreLeftForTheAi()
    {
        var h = new Harness();
        h.Type("place a bst now ");   // "bat" and "bet" are equally likely slips
        Assert.Equal("place a bst now ", h.Text);
    }

    [Fact]
    public void R4_3_c_AdjacentSwapIsAnObviousFix()
    {
        var h = new Harness();
        h.Type("we wnet home ");
        Assert.Equal("we went home ", h.Text);
    }

    [Fact]
    public void R4_3_d_CloseCandidatesAreLeftForTheAi()
    {
        var h = new Harness();
        h.Type("cheers ofr that ");
        Assert.Equal("cheers ofr that ", h.Text);
    }

    [Fact]
    public void R4_3_d_RunTogetherWordsAreLeftForTheAi()
    {
        var h = new Harness();
        h.Type("ask me ifyou need ");
        Assert.Equal("ask me ifyou need ", h.Text);
    }

    [Fact]
    public void R4_3_d_HalfOfASplitWordIsLeftForTheAi()
    {
        var h = new Harness();
        h.Type("a half eaten s andwich ");   // "s" + "andwich" is "sandwich": a misplaced space
        Assert.Equal("a half eaten s andwich ", h.Text);
    }

    [Fact]
    public void R4_4_c_KeepsTheSpaceTheUserJustTyped()
    {
        // The model trims its reply; the space after the full stop must survive so the next word isn't glued on.
        var h = new Harness(model: s => s.Trim().Replace("i went their", "I went there"));
        h.Type("i went their yesterday. ");
        h.Wait(600);
        h.Type("Then");
        Assert.Equal("I went there yesterday. Then", h.Text);
    }

    [Fact]
    public void R4_4_d_NoFullStopAddedToAnUnfinishedSentence()
    {
        var h = new Harness(model: s => s.Replace("i went their yesterday", "I went there yesterday."), latencyMs: 50);
        h.Type("i went their yesterday");
        h.Wait(1700);
        Assert.Equal("I went there yesterday", h.Text);
    }

    [Fact]
    public void R4_3_c_KeepsCapitalisationPattern()
    {
        var h = new Harness();
        h.Type("Done. Teh end ");
        Assert.Equal("Done. The end ", h.Text);
    }

    // ---- Only the user's own typing (4.2, 14.4) ----------------------------------------------------

    [Fact]
    public void R4_2_PastedTextIsNeverCorrected()
    {
        var h = new Harness(model: s => s.Replace("teh", "the"));
        h.Engine.OnInput(h.Ed.Paste("I saw teh cat. "));
        h.Type("Then left. ");
        h.Wait(3000);
        Assert.StartsWith("I saw teh cat. ", h.Text);
    }

    [Fact]
    public void R4_2_ForeignInsertedTextBreaksTheSessionAndIsNotCorrected()
    {
        var h = new Harness(model: s => s.Replace("teh", "the"));
        h.Type("hello ");
        h.Engine.OnInput(h.Ed.ForeignInsert("teh dictated "));
        h.Type("more ");
        h.Wait(3000);
        Assert.Contains("teh dictated", h.Text);
        Assert.Equal(SessionBreakReason.Foreign, h.Engine.Session.LastBreak);
    }

    // ---- AI layer (4.4) --------------------------------------------------------------------------

    private static string FixTheir(string s) =>
        s.Replace("i went their", "I went there").Replace("their yesterday", "there yesterday");

    [Fact]
    public void R4_4_a_TriggersAtSentenceEnd()
    {
        var h = new Harness(model: FixTheir);
        h.Type("i went their yesterday. ");
        Assert.Single(h.Llm!.Requests);
        h.Wait(600);
        Assert.Equal("I went there yesterday. ", h.Text);
    }

    [Fact]
    public void R4_4_a_TriggersAfterPauseButNotWhileTyping()
    {
        var h = new Harness(model: FixTheir, latencyMs: 100);
        h.Type("i went their yesterday");
        Assert.Empty(h.Llm!.Requests);
        h.Wait(650);
        Assert.Empty(h.Llm.Requests);
        h.Wait(100);
        Assert.Single(h.Llm.Requests);
    }

    [Fact]
    public void R4_6_b_LastTwoWordsWaitForA1500msPause_R4_6_a()
    {
        var h = new Harness(model: FixTheir, latencyMs: 100);
        h.Type("i went their yesterday");
        h.Wait(1000);
        // "i" is fixable already; "their" is one of the last two words and still protected.
        Assert.Equal("I went their yesterday", h.Text);
        h.Wait(600);
        Assert.Equal("I went there yesterday", h.Text);
    }

    [Fact]
    public void R4_4_b_SendsCurrentSentencePlusTwoBefore()
    {
        var h = new Harness(model: s => s);
        h.Type("One here. Two here. Three here. Four here. ");
        Assert.Equal("Two here. Three here. Four here. ", h.Llm!.Requests[^1]);
    }

    [Fact]
    public void R4_4_e_WritesSmallSeparateEditsNotTheWholeSpan()
    {
        var h = new Harness(model: s => s.Replace("i went", "I went").Replace("tomorow", "tomorrow"));
        h.Type("i went there tomorow. ");
        h.Wait(1000);
        Assert.Equal("I went there tomorrow. ", h.Text);
        Assert.Equal(2, h.Ed.Edits);
    }

    [Fact]
    public void R4_4_d_RejectsRewrites()
    {
        var h = new Harness(model: s => s.Replace("I feel happy", "I am glad"));
        h.Type("I feel happy today. ");
        h.Wait(1000);
        Assert.Equal("I feel happy today. ", h.Text);
    }

    // ---- Timing and caret rules (4.6, 12.2, 12.3) ------------------------------------------------

    [Fact]
    public void R4_6_c_NoAiWriteWhileKeysKeepComing_ThenRebasesAfterAGap()
    {
        var h = new Harness(model: FixTheir, latencyMs: 200);
        h.Type("i went their yesterday. ");
        h.Type("And then I came home", msPerKey: 100);   // the result arrives mid-typing; gaps are only 100 ms
        Assert.StartsWith("i went their", h.Text);
        h.Wait(300);
        Assert.Equal("I went there yesterday. And then I came home", h.Text);
    }

    [Fact]
    public void R12_3_AiRunsWhileUserTypes_TypingIsNotBlocked()
    {
        var h = new Harness(model: FixTheir);
        h.Llm!.Hang = true;   // the model never answers
        var sw = Stopwatch.StartNew();
        h.Type("i went their yesterday. and more words here ");
        sw.Stop();
        Assert.EndsWith("and more words here ", h.Text);
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public void R12_2_NoWriteWhenAKeyArrivedSinceTheDecision()
    {
        var h = new Harness();
        h.Type("we saw teh");
        var space = h.Ed.TypeChar(' ');
        h.Ed.TypeUndelivered('c');   // the hook saw 'c' but the engine hasn't processed it
        h.Engine.OnInput(space);
        Assert.Equal("we saw teh c", h.Text);   // nothing written while the view is stale
        h.Engine.OnInput(new KeyInput(KeyKind.Char, 'c', h.Ed.LatestKeySeq, h.Clock.NowMs));
        h.Wait(200);
        Assert.Equal("we saw the c", h.Text);   // written later, at the right place
    }

    [Fact]
    public void R4_6_d_DesyncStopsCorrectingThatField()
    {
        var h = new Harness();
        h.Ed.BeforeApply = ed => { ed.Text = ed.Text.Insert(ed.Caret, "x"); ed.Caret++; };   // something changes the field mid-write
        h.Type("we saw teh ");
        Assert.Equal(1, h.Counters.Desyncs);
        h.Ed.BeforeApply = null;
        h.Type("and teh ");
        Assert.EndsWith("and teh ", h.Text);
    }

    [Fact]
    public void R4_6_d_TypingDuringVerificationIsNotADesync()
    {
        // Found by E2E: the user keeps typing while the app shows our edit; that must not count as a desync.
        var h = new Harness();
        h.Ed.BeforeApply = ed => ed.TypedDuringVerify = "do";
        h.Type("we saw teh ");
        Assert.Equal(0, h.Counters.Desyncs);
        Assert.Equal(1, h.Counters.InstantCorrections);
        Assert.StartsWith("we saw the ", h.Text);
    }

    [Fact]
    public void R4_6_e_WriteWaitsButTypingDoesNot()
    {
        var h = new Harness(model: FixTheir, latencyMs: 50);
        h.Type("i went their yesterday. ");
        h.Ed.ModifierHeld = true;   // e.g. Shift held: the write waits
        h.Wait(500);
        Assert.StartsWith("i went", h.Text);
        h.Type("ok");
        h.Ed.ModifierHeld = false;
        h.Wait(1700);
        Assert.StartsWith("I went there", h.Text);
    }

    [Fact]
    public void R4_4_a_AMomentaryReadFailureAtTheSentenceEndIsRetried()
    {
        // Found by E2E in ProseMirror: one failed read used up the trigger and the AI never ran.
        var h = new Harness(model: FixTheir, latencyMs: 100);
        h.Type("i went their yesterday");
        h.Ed.FailNextReads = 1;
        h.Type(". ");
        h.Wait(2500);
        Assert.Equal("I went there yesterday. ", h.Text);
    }

    [Fact]
    public void R4_6_c_AMomentaryReadFailureBeforeAWriteDoesNotLoseTheEdit()
    {
        // Found by E2E in Notepad: a failed read just before writing dropped "their" -> "there".
        var h = new Harness(model: FixTheir, latencyMs: 100);
        h.Type("i went their yesterday. ");
        h.Clock.Advance(150);
        h.Ed.FailNextReads = 2;   // the reads right before the writes fail once or twice
        h.Wait(2500);
        Assert.Equal("I went there yesterday. ", h.Text);
        Assert.Equal(0, h.Counters.DroppedWrites);
    }

    [Fact]
    public void R4_1_b_APasswordFieldIsNotPolledForever()
    {
        var h = new Harness(model: FixTheir);
        h.Ed.CaretKnown = false;
        h.Type("i went their yesterday");
        h.Wait(10_000);
        Assert.Empty(h.Llm!.Requests);   // gave up after a few reads; nothing sent
    }

    [Fact]
    public void R4_1_b_CaretUnknownMeansNothingIsWritten()
    {
        var h = new Harness(model: FixTheir);
        h.Ed.CaretKnown = false;
        h.Type("i went their yesterday. teh ");
        h.Wait(2000);
        Assert.Equal("i went their yesterday. teh ", h.Text);
        Assert.Empty(h.Llm!.Requests);
    }

    [Fact]
    public void R10_2_HostAutocorrectIsDetectedNotDoubled()
    {
        var h = new Harness();
        h.Ed.HostAutocorrect = w => w == "teh" ? "the" : null;
        h.Type("we saw teh cat ");
        Assert.Equal("we saw the cat ", h.Text);
        Assert.Equal(0, h.Ed.Edits);
    }

    [Fact]
    public void R10_2_HostAutoCapitalisationIsAdoptedNotABreak()
    {
        var h = new Harness();
        h.Ed.HostAutocorrect = w => w == "hello" ? "Hello" : null;
        h.Type("hello there teh ");
        Assert.Equal("Hello there the ", h.Text);
    }

    // ---- Undo (4.8) ----------------------------------------------------------------------------

    [Fact]
    public void R4_8_a_LocalUndoRevertsAsOneStep_AndR4_8_c_LearnsTheWord()
    {
        var h = new Harness();
        h.Type("we saw teh ");
        Assert.Equal("we saw the ", h.Text);
        Assert.Equal(UndoResult.Reverted, h.Engine.Undo(global: false));
        Assert.Equal("we saw teh ", h.Text);
        Assert.Contains("teh", h.Personal);
        h.Type("teh ");
        Assert.Equal("we saw teh teh ", h.Text);   // 4.8.c: never corrected again
    }

    [Fact]
    public void R4_8_b_GlobalUndoWorksAfterTheCaretMoved()
    {
        var h = new Harness();
        h.Type("we saw teh cat ");
        h.Engine.OnInput(h.Ed.MoveCaret(-2));   // caret moved: the session breaks
        Assert.Equal(UndoResult.TextChanged, h.Engine.Undo(global: false));
        Assert.Equal(UndoResult.Reverted, h.Engine.Undo(global: true));
        Assert.Equal("we saw teh cat ", h.Text);
    }

    [Fact]
    public void R4_8_b_GlobalUndoDoesNothingInAnotherField()
    {
        var h = new Harness();
        h.Type("we saw teh ");
        h.Ed.Id = "other-field";
        Assert.Equal(UndoResult.OtherField, h.Engine.Undo(global: true));
    }

    [Fact]
    public void R4_8_b_GlobalUndoDoesNothingIfTheTextChanged()
    {
        var h = new Harness();
        h.Type("we saw teh ");
        h.Engine.OnInput(h.Ed.Backspace());
        h.Engine.OnInput(h.Ed.Backspace());
        h.Engine.OnInput(h.Ed.Backspace());
        Assert.Equal(UndoResult.TextChanged, h.Engine.Undo(global: true));
    }

    // ---- Learning: auto-learn (8.5) and prediction history (7.3) ------------------------------------------

    [Fact]
    public void R8_5_AnUnrecognisedWordThatSurvivesCountsTowardsAutoLearn()
    {
        var h = new Harness();
        h.Type("we met Zorblat today ");   // a capitalised name: the instant layer leaves it alone
        Assert.Empty(h.Noted);             // not straight away
        h.Wait(5500);
        Assert.Contains("Zorblat", h.Noted);
    }

    [Fact]
    public void R8_5_TyposTheAiFixesAreNeverLearnt()
    {
        var h = new Harness(model: s => s.Replace("ofr", "for"), latencyMs: 100);
        h.Type("cheers ofr that. ");
        h.Wait(6000);
        Assert.Equal("cheers for that. ", h.Text);
        Assert.DoesNotContain("ofr", h.Noted);
    }

    [Fact]
    public void R7_3_HistoryLearnsOnlyRealWordsAsSingleWords()
    {
        var h = new Harness();
        h.Type("the quick zzqx fox ");
        Assert.Equal(["the", "quick", "fox"], h.Observed);
    }

    [Fact]
    public void R8_3_NothingIsLearntFromAFieldTheAppMayNotRead()
    {
        var h = new Harness();
        h.Ed.CaretKnown = false;   // password field, excluded app, elevated window: all read as "not OK"
        h.Type("my secret Zorblat words ");
        h.Wait(6000);
        Assert.Empty(h.Observed);
        Assert.Empty(h.Noted);
    }

    [Fact]
    public void R8_2_NothingIsLearntWhilePaused()
    {
        var h = new Harness();
        h.Engine.Paused = true;
        h.Type("the quick Zorblat ");
        h.Wait(6000);
        Assert.Empty(h.Observed);
        Assert.Empty(h.Noted);
    }

    [Fact]
    public void R8_3_AcceptingASuggestionNeverTypesIntoAFieldItMayNotRead_R7_4()
    {
        var h = new Harness();
        h.Type("hello ");
        h.Ed.CaretKnown = false;   // focus moved to a password box
        Assert.False(h.Engine.InsertAtCaret("world"));
        Assert.Equal("hello ", h.Text);
        h.Ed.CaretKnown = true;
        Assert.True(h.Engine.InsertAtCaret("world"));
        Assert.Equal("hello world", h.Text);
    }

    // ---- Telemetry and privacy (6.5, 8.6) -----------------------------------------------------------

    [Fact]
    public void R6_5_CountsCorrectionsAndUndos()
    {
        var h = new Harness();
        h.Type("we saw teh bookk ");
        h.Engine.Undo(global: false);
        Assert.Equal(2, h.Counters.InstantCorrections);
        Assert.Equal(1, h.Counters.Undos);
    }

    [Fact]
    public void R8_6_CountersHoldNoText()
    {
        var h = new Harness();
        h.Type("we saw teh secretword ");
        string json = h.Counters.ToJson();
        Assert.DoesNotContain("secretword", json);
        Assert.DoesNotContain("teh", json);
        Assert.Matches("^[{}\":,a-zA-Z0-9]+$", json);
    }

    [Fact]
    public void R0_2_c_AppliesEveryAcceptedEdit()
    {
        var h = new Harness(model: s => s.Replace("i went", "I went").Replace("their", "there").Replace("yesturday", "yesterday"));
        h.Type("i went their yesturday. ");
        h.Wait(1000);
        Assert.Equal("I went there yesterday. ", h.Text);
    }
}
