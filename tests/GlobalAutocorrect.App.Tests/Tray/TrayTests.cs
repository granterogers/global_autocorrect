using GlobalAutocorrect.App.Tray;
using Microsoft.Win32;
using Xunit;

namespace GlobalAutocorrect.App.Tests.Tray;

public class TrayTests
{
    private const string TestKey = @"Software\GlobalAutocorrectTests\Run";
    private const string FakeExe = @"C:\Program Files\Global Autocorrect\GlobalAutocorrect.exe";

    [Fact]
    public void R8_1_StartupRegistrationRoundTrip()
    {
        try
        {
            var reg = new StartupRegistration(TestKey, FakeExe);
            Assert.False(reg.IsEnabled);

            Assert.True(reg.Apply(true));
            Assert.True(reg.IsEnabled);
            using (var key = Registry.CurrentUser.OpenSubKey(TestKey))
                Assert.Equal("\"" + FakeExe + "\"", key!.GetValue(StartupRegistration.ValueName) as string);

            // Applying twice is harmless, and a new instance sees the same state.
            Assert.True(reg.Apply(true));
            Assert.True(new StartupRegistration(TestKey, FakeExe).IsEnabled);

            Assert.True(reg.Apply(false));
            Assert.False(reg.IsEnabled);
            using (var key = Registry.CurrentUser.OpenSubKey(TestKey))
                Assert.Null(key?.GetValue(StartupRegistration.ValueName));

            // Turning it off when nothing was written must not throw.
            Assert.True(new StartupRegistration(@"Software\GlobalAutocorrectTests\DoesNotExist", FakeExe).Apply(false));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\GlobalAutocorrectTests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void R8_1_StartupRegistrationDefaultsToTheUserRunKey()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistration.DefaultKeyPath);
        Assert.Equal("GlobalAutocorrect", StartupRegistration.ValueName);
    }

    [Fact]
    public void R8_2_TooltipReflectsPausedState()
    {
        Assert.Equal("Global Autocorrect \u2014 on", TrayController.TooltipFor(false));
        Assert.Equal("Global Autocorrect \u2014 paused", TrayController.TooltipFor(true));
    }
}
