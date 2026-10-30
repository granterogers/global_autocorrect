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
