using System.Runtime.InteropServices;
using GlobalAutocorrect.Windows.Native;
using Xunit;

namespace GlobalAutocorrect.App.Tests;

/// <summary>
/// D182, found in a health check: this PC had been up 3.7 days and asleep for 2.85 of them, and every idle reading was
/// zero, because the last input was compared with Environment.TickCount, which doesn't count time asleep.
/// </summary>
public sealed class IdleClockTests
{
    [Fact]
    public void R13_3_D182_TheLastInputIsNeverAheadOfTheClockIdleTimeIsReadAgainst()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        Assert.True(NativeMethods.GetLastInputInfo(ref info));
        int ahead = unchecked((int)(info.dwTime - NativeMethods.GetTickCount()));
        Assert.True(ahead <= 1000, $"the last input reads as {ahead} ms in the future");   // a hair of skew is fine (D153)
    }
}
