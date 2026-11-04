using GlobalAutocorrect.Core.Instant;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Instant;

/// <summary>
/// D55: spacing and punctuation tidied the moment the next space is typed, with no AI. Each case is the text typed so
/// far (ending with the space or Enter that triggers the check) and the text after the fix, or null when it must be
/// left alone.
/// </summary>
public class TidyRulesTests
{
    private static readonly Lazy<TidyRules> Rules = new(() => new TidyRules(HunspellSpellChecker.FromAssets(HunspellTests.Assets())));

    private static string? After(string typed) =>
        Rules.Value.Find(typed) is { } f ? typed[..f.Start] + f.Replacement + typed[f.End..] : null;

    [Theory]
    [InlineData("My colleague can join in tomorrow5th ", "My colleague can join in tomorrow 5th ")]   // the user's Intercom reply
    [InlineData("on the21st ", "on the 21st ")]
    [InlineData("Shall we say at1500 ", "Shall we say at 1500 ")]
    [InlineData("see you on Friday12th ", "see you on Friday 12th ")]
    [InlineData("call me by3pm ", "call me by 3pm ")]
    [InlineData("due November5. ", "due November 5. ")]
    [InlineData("on the 5thNovember ", "on the 5th November ")]
    public void R4_3_D82_AWordAndANumberTypedTogetherGetTheirSpace(string typed, string expected) => Assert.Equal(expected, After(typed));

    [Theory]
    [InlineData("upgrade to Windows11 ")]   // names and codes keep their digits attached
    [InlineData("the mp3 ")]
    [InlineData("covid19 ")]
    [InlineData("use office365 ")]
    [InlineData("the tomorrow5rd ")]         // a wrong ordinal: not sure what was meant
    [InlineData("ref abc123 ")]
    [InlineData("the 5thxq ")]
    public void R4_3_D82_NamesAndCodesWithDigitsAreLeftAlone(string typed) => Assert.Null(After(typed));

    [Fact]
    public void R4_3_D82_TheSpaceIsPutInWhileTyping_InTheField()
    {
        var h = new Simulated.Harness(options: new GlobalAutocorrect.Core.Engine.EngineOptions());   // spacing rules on, as installed
        h.Type("My colleague can join in tomorrow5th November ");
        Assert.Equal("My colleague can join in tomorrow 5th November ", h.Text);
    }

    [Theory]
    [InlineData("to see what the next stes . ", "to see what the next stes. ")]   // the user's Intercom reply
    [InlineData("thanks , ", "thanks, ")]
    [InlineData("really ? ", "really? ")]
    [InlineData("really ?! ", "really?! ")]
    [InlineData("as follows : ", "as follows: ")]
    [InlineData("the end .\n", "the end.\n")]
    public void R4_3_D55_SpaceBeforePunctuationIsRemoved(string typed, string expected) => Assert.Equal(expected, After(typed));

    [Theory]
    [InlineData("liaise with  theServicees ", "liaise with theServicees ")]   // the user's Intercom reply
    [InlineData("the other dy  and ", "the other dy and ")]                 // the user's first long paragraph
    [InlineData("yes,  please ", "yes, please ")]
    public void R4_3_D55_DoubleSpaceBetweenWordsIsOneSpace(string typed, string expected) => Assert.Equal(expected, After(typed));

    [Theory]
    [InlineData("That was the end.Next ", "That was the end. Next ")]
    [InlineData("we agreed.Then ", "we agreed. Then ")]
    [InlineData("apples,pears ", "apples, pears ")]
    public void R4_3_D55_MissingSpaceAfterPunctuationIsAdded(string typed, string expected) => Assert.Equal(expected, After(typed));

    [Theory]
    [InlineData("yes i ", "yes I ")]
    [InlineData("i ", "I ")]                         // the very first thing typed (this crashed the stress run)
    [InlineData("ok\ni ", "ok\nI ")]                 // first on a line
    [InlineData("i'm sure ", null)]                 // only the word just finished is looked at
    [InlineData("and i'm ", "and I'm ")]
    [InlineData("so i, ", "so I, ")]
    [InlineData("so i. ", "so I. ")]
    [InlineData("we thought. then ", "we thought. Then ")]
    [InlineData("Really? yes ", "Really? Yes ")]
    public void R4_3_D55_CapitalIAndSentenceStarts(string typed, string? expected) => Assert.Equal(expected, After(typed));

    [Theory]
    [InlineData("we met. THe ", "we met. The ")]
    [InlineData("and WHen ", "and When ")]
    [InlineData("access, etc.\n\nIF ", "access, etc.\n\nIf ")]   // the user's Intercom reply
    [InlineData("done. IN ", "done. In ")]
    public void R4_3_D78_TwoInitialCapitalsAreOneCapital(string typed, string expected) => Assert.Equal(expected, After(typed));

    [Theory]
    [InlineData("call the IT ")]                    // abbreviations stay
    [InlineData("done. US ")]
    [InlineData("done. OK ")]
    [InlineData("PLEASE READ. IF ")]                // the text around it is in capitals: shouting, not a slip
    [InlineData("and IF ")]                         // mid-sentence two letters: could be meant
    [InlineData("the NHs ")]                        // not a word with one capital
    public void R4_3_D78_CapitalsThatMayBeMeantStay(string typed) => Assert.Null(After(typed));

    [Theory]
    [InlineData("use .NET ")]                       // a leading dot is part of a name
    [InlineData("great :) ")]                       // emoticons
    [InlineData("wink ;) ")]
    [InlineData("and so on ... ")]                  // an ellipsis
    [InlineData("score 5 . ")]                      // numbers
    [InlineData("first sentence.  Second ")]        // two spaces after a full stop is a typing style, not a slip
    [InlineData("    indented ")]                   // indentation
    [InlineData("a  b   c ")]                       // three spaces: alignment
    [InlineData("see index.html ")]                 // file names, URLs, e.g.
    [InlineData("visit example.com ")]
    [InlineData("for example e.g. this ")]
    [InlineData("ok, e.g. this ")]
    [InlineData("Mr. smith ")]                      // abbreviations before a lower-case word
    [InlineData("wait... what ")]
    [InlineData("for i in items ")]                 // code
    [InlineData("x = i ")]
    [InlineData("ok\nthanks ")]                     // a new line in chat is not a new sentence
    [InlineData("ok thanks ")]                      // the first word typed is never capitalised (it may be mid-sentence)
    [InlineData("we agreed.iPhone ")]
    [InlineData("we agreed.Xyzzq ")]                // not a word after the dot
    [InlineData("red,green ")]                      // short words with a comma (under 4 letters): lists and data
    public void R4_3_D55_LeftAlone(string typed) => Assert.Null(After(typed));
}
