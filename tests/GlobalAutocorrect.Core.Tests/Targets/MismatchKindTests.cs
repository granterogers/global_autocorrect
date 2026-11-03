using GlobalAutocorrect.Core.Targets;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Targets;

public class MismatchKindTests
{
    [Fact]
    public void R8_6_D76_AMismatchIsDescribedByKindsOfCharacterNeverByText()
    {
        // An embedded object in the field where the user typed a space, 6 characters before the caret.
        string d = MismatchKind.Describe("thanks again", "thanks￼again");
        Assert.Equal("typed=12 field=12 sameFromCaret=5 typedHas=space fieldHas=object fieldObjects=1 fieldNbsp=0 fieldZeroWidth=0", d);
        Assert.DoesNotContain("thanks", d);
        Assert.DoesNotContain("again", d);
    }

    [Theory]
    [InlineData('a', "letter")]
    [InlineData('A', "capital")]
    [InlineData(' ', "nbsp")]
    [InlineData('​', "zero-width")]
    [InlineData('.', "punctuation")]
    public void R8_6_D76_CharacterKinds(char c, string kind) => Assert.Equal(kind, MismatchKind.Of(c));
}
