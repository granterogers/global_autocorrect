using GlobalAutocorrect.Core.Targets;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D109: CKEditor (Teams) shows a word joiner beside the soft line break Shift+Enter makes.</summary>
public sealed class SoftLineBreakTests
{
    [Fact]
    public void R4_6_d_D109_AWordJoinerByALineBreakDoesNotMakeTheTextDiffer() =>
        Assert.Equal(TextNorm.Comparable("the strike?\nHi Paul"), TextNorm.Comparable("the strike?\n⁠Hi Paul"));
}
