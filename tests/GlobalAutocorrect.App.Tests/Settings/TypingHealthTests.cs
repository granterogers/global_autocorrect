using GlobalAutocorrect.App.ViewModels;
using Xunit;

namespace GlobalAutocorrect.App.Tests.Settings;

/// <summary>D97: the Home page says, in plain words, where the app is doing less to keep typing smooth.</summary>
public sealed class TypingHealthTests
{
    [Fact]
    public void R9_3_a_D97_HomeSaysWhereTheAppIsDoingLess()
    {
        Assert.Equal("Typing: smooth everywhere", HomeViewModel.DescribeTyping([]));
        Assert.Equal("Doing less in claude, msedge to keep typing smooth (slow to read)", HomeViewModel.DescribeTyping(["claude", "msedge"]));
    }
}
