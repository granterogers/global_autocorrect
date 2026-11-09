using GlobalAutocorrect.Windows.Write;
using Xunit;

namespace GlobalAutocorrect.Windows.Tests;

/// <summary>D108, user report "why the capitals?": Caps Lock is read as the app in front sees it, not from a stale thread state.</summary>
public sealed class CapsLockTests
{
    [Fact]
    public void R4_6_d_D108_CapsLockIsReadFromTheForegroundAppsInputState()
    {
        // Matches the console's view (also the foreground input state on an interactive desktop); never throws when
        // nothing is in front (a service or CI runner).
        bool caps = SendInputWriter.CapsLockOn();
        if (OperatingSystem.IsWindows() && Environment.UserInteractive) Assert.Equal(Console.CapsLock, caps);
    }
}
