using GlobalAutocorrect.Core.Targets;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Targets;

public class TextNormTests
{
    [Theory]
    // Edge's suggestion shown when the field was read before the write, cleared by the edit (D61, random-typing test).
    [InlineData(" suggestion ptemorning tomorrow at nine", " ptemorning tomorrow at nine and then", true)]
    [InlineData(" for the rest of the week ptemorning tomor", " ptemorning tomorrow at nine", true)]   // a long one
    // The other way round: no suggestion before, one after (D42).
    [InlineData(" ptemorning tomorrow at nine", " suggestion ptemorning tomorrow at nine", true)]
    [InlineData("", "anything", true)]
    // Genuinely different text after the caret is still a mismatch.
    [InlineData(" the dog sat on the mat today", " a cat lay down by the door", false)]
    [InlineData(" the dog sat on the mat today", " the cat sat on the mat today", false)]
    public void R4_6_d_D61_TextAfterTheCaretIsConsistentAroundASuggestion(string expected, string actual, bool consistent) =>
        Assert.Equal(consistent, TextNorm.AfterConsistent(expected, actual));
}
