using GlobalAutocorrect.Core.Targets;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D151 (Teams, 20 Nov: "releaserelease"): a placed fix that went in beside the word is found and can be taken out.</summary>
public sealed class StrayCopyTests
{
    [Fact]
    public void R4_6_d_D151_TheFixTwiceInARowLeavesOneOut()
    {
        var (at, kept) = TextNorm.StrayCopy("which I now releaserelease ", "release", "which I now release ", "which I now relase ", 64);
        Assert.True(at >= 0);
        Assert.True(kept);
        Assert.Equal("which I now release ", "which I now releaserelease ".Remove(at, 7));
    }

    [Fact]
    public void R4_6_d_D151_TheFixBesideTheUnfixedWordGoesBackToHowItWas()
    {
        var (at, kept) = TextNorm.StrayCopy("which I now relase release", "release", "which I now release ", "which I now relase ", 64);
        Assert.Equal("which I now relase ", "which I now relase release".Remove(at, 7));
        Assert.False(kept);
    }

    [Fact]
    public void R4_6_d_D151_WhatTheUserTypedSinceIsLeftAlone()
    {
        string now = "which I now releaserelease next Thu";
        var (at, kept) = TextNorm.StrayCopy(now, "release", "which I now release ", "which I now relase ", 64);
        Assert.True(kept);
        Assert.Equal("which I now release next Thu", now.Remove(at, 7));
    }

    [Theory]
    [InlineData("which I now relaxe ")]                    // something else went wrong: nothing to take out
    [InlineData("which I now release release ")]           // two words, not a stray copy: taking one out leaves neither text
    public void R4_6_d_D151_NoSingleStrayCopyNoChange(string now)
    {
        Assert.Equal(-1, TextNorm.StrayCopy(now, "release", "which I now release ", "which I now relase ", 64).Index);
    }
}
