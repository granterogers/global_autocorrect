using GlobalAutocorrect.App;
using Xunit;

namespace GlobalAutocorrect.App.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void R5_6_c_OnlyOneCopyRuns()
    {
        string name = @"Local\GlobalAutocorrect.Test." + Guid.NewGuid().ToString("N");
        using var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(name));
    }

    [Fact]
    public void R13_4_TheInstallerCanAskTheRunningCopyToExit()
    {
        // Updates replace the files while the app is running; it must close the way the tray's Exit does, not be killed.
        string name = @"Local\GlobalAutocorrect.TestExit." + Guid.NewGuid().ToString("N");
        Assert.False(SingleInstance.SignalExit(name));   // nothing running
        using var signal = SingleInstance.CreateExitSignal(name);
        Assert.False(signal.WaitOne(0));
        Assert.True(SingleInstance.SignalExit(name));
        Assert.True(signal.WaitOne(1000));
    }
}
