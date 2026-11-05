using GlobalAutocorrect.Core.Tests.Simulated;
using GlobalAutocorrect.Core.Text;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D96, user report (Intercom, 2026-11-05): "Are you able to jump in? IT shouldn't take long." — "IT should of
/// course be It": Shift held into the second letter.</summary>
public sealed class HeldShiftTests
{
    [Theory]
    [InlineData("IT", "It")]
    [InlineData("THe", "The")]
    [InlineData("WHen", "When")]
    [InlineData("IDs", null)]      // a plural acronym
    [InlineData("NASA", null)]
    [InlineData("The", null)]
    [InlineData("iPhone", null)]
    public void R4_4_d_D96_ShiftHeldIntoTheSecondLetter(string typed, string? meant) =>
        Assert.Equal(meant, Capitals.HeldShift(typed));

    [Fact]
    public void R4_4_d_D96_TheModelsITToItAtASentenceStartIsAccepted()
    {
        var h = new Harness(model: s => s.Replace("IT shouldn't", "It shouldn't"), latencyMs: 50);
        h.Type("Are you able to jump in? IT shouldn't take long. ");
        h.Wait(1500);
        Assert.Equal("Are you able to jump in? It shouldn't take long. ", h.Text);
    }

    [Fact]
    public void R4_4_d_D96_ITMidSentenceIsTheUsersWord()
    {
        var h = new Harness(model: s => s.Replace("the IT team", "the It team"), latencyMs: 50);
        h.Type("Ask the IT team to look. ");
        h.Wait(1500);
        Assert.Equal("Ask the IT team to look. ", h.Text);
    }

    [Theory]
    [InlineData("THe cat sat ", "The cat sat ")]
    [InlineData("we went to LOndon ", "we went to London ")]
    [InlineData("send the IDs ", "send the IDs ")]
    public void R4_3_D96_AHeldShiftIsFixedAtOnceWhenTheWordIsntOneAsTyped(string typed, string expected)
    {
        var h = new Harness();
        h.Type(typed);
        h.Wait(300);
        Assert.Equal(expected, h.Text);
    }
}
