using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D92, user request (Intercom, 2026-11-05): "Reply Christopher Moyer" is shown above a reply that began
/// "Hi Chrisopher,". A slip of a name the app shows near the field becomes that name.</summary>
public sealed class NamesNearbyTests
{
    [Fact]
    public void R4_3_D92_AMisspeltNameShownNearTheFieldIsFixed()
    {
        var h = new Harness();
        h.Ed.NamesNearby = NameFix.Candidates(["Reply", "Christopher Moyer"]);
        h.Type("Hi Chrisopher, thanks ");
        h.Wait(300);
        Assert.Equal("Hi Christopher, thanks ", h.Text);
    }

    [Fact]
    public void R4_3_c_D92_WithoutTheNameOnScreenACapitalisedWordIsLeftAlone()
    {
        var h = new Harness();
        h.Type("Hi Chrisopher, thanks ");
        h.Wait(300);
        Assert.Equal("Hi Chrisopher, thanks ", h.Text);
    }

    [Theory]
    [InlineData("Chrisopher", "Christopher")]
    [InlineData("christpoher", "Christopher")]   // a swap, typed in lower case
    [InlineData("CHRISOPHER", "CHRISTOPHER")]
    [InlineData("Moyre", "Moyer")]               // one slip in a short name
    [InlineData("Christopher", null)]            // already right
    [InlineData("Mayer", null)]                  // a name of its own: o and a aren't neighbouring keys
    [InlineData("Kristopher", null)]             // another first letter: another name
    [InlineData("Other", null)]                  // a word: left alone
    public void R4_3_D92_OnlyAClearSlipOfOneNameIsFixed(string typed, string? expected)
    {
        var names = NameFix.Candidates(["Reply", "Christopher Moyer", "Open 12:30"]);
        Assert.Equal(expected, NameFix.For(typed, names, w => w is "other" or "Other" or "reply"));
    }

    [Fact]
    public void R4_3_D92_TwoNamesEquallyCloseAreNotGuessedBetween()
    {
        var names = NameFix.Candidates(["Jonathon Smith", "Jonathan Jones"]);
        Assert.Null(NameFix.For("Jonathen", names, _ => false));
    }

    [Fact]
    public void R8_6_D92_OnlyCapitalisedWordsAreKept_NotNumbersOrCode()
    {
        var names = NameFix.Candidates(["Christopher Moyer", "sent 12:30", "ticket #4471", "derek@example.com", "Ab"]);
        Assert.Equal(["Christopher", "Moyer"], names);
    }
}
