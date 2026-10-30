using GlobalAutocorrect.Windows.Uia;
using Xunit;

namespace GlobalAutocorrect.Windows.Tests;

public class FocusFilterTests
{
    private const uint Focus = 0x8005, Foreground = 0x0003;

    [Fact]
    public void R4_6_a_D71_TheSameElementFocusedAgainIsNotAFocusChange()
    {
        // Chromium repeats the focus event for the focused element mid-typing; it broke the session mid-word.
        var f = new FocusFilter();
        Assert.True(f.IsNew(Focus, 100, -4, 52));
        Assert.False(f.IsNew(Focus, 100, -4, 52));
        Assert.False(f.IsNew(Focus, 100, -4, 52));
    }

    [Fact]
    public void R4_6_a_D71_AnotherElementOrWindowIsAFocusChange()
    {
        var f = new FocusFilter();
        Assert.True(f.IsNew(Focus, 100, -4, 52));
        Assert.True(f.IsNew(Focus, 100, -4, 53));        // another element in the same page
        Assert.True(f.IsNew(Foreground, 200, 0, 0));     // another window
        Assert.True(f.IsNew(Focus, 200, -4, 7));
        Assert.True(f.IsNew(Foreground, 100, 0, 0));     // and back again
        Assert.True(f.IsNew(Focus, 100, -4, 52));        // the first field again, after being elsewhere
    }
}
