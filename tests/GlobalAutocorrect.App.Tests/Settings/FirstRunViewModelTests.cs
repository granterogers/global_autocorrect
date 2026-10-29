using System.Net.Http;
using GlobalAutocorrect.App.ViewModels;
using Xunit;

namespace GlobalAutocorrect.App.Tests.Settings;

public class FirstRunViewModelTests
{
    [Fact]
    public void R9_4_FirstRunMarksDoneOnFinish()
    {
        using var s = new FakeServices();
        var vm = new FirstRunViewModel(s, new FakeInstaller { Installed = true });
        bool finished = false;
        vm.Finished += () => finished = true;

        Assert.False(s.Settings.FirstRunDone);
        Assert.True(vm.IsStep1);
        Assert.True(vm.IsDone);   // the model is already on this PC

        vm.NextCommand.Execute(null);
        Assert.True(vm.IsStep2);
        Assert.True(vm.CanGoNext);   // already installed: Next is enabled
        vm.NextCommand.Execute(null);
        Assert.True(vm.IsStep3);
        Assert.True(vm.IsLastStep);
        Assert.False(s.Settings.FirstRunDone);   // not until Finish

        vm.FinishCommand.Execute(null);
        Assert.True(s.Settings.FirstRunDone);
        Assert.True(finished);
    }

    [Fact]
    public async Task R9_4_FirstRunWaitsForTheModelDownload()
    {
        using var s = new FakeServices();
        var installer = new FakeInstaller();
        var vm = new FirstRunViewModel(s, installer);

        vm.NextCommand.Execute(null);
        Assert.True(vm.IsStep2);
        Assert.True(vm.IsIdle);
        Assert.False(vm.CanGoNext);   // can't skip the download
        vm.NextCommand.Execute(null);
        Assert.True(vm.IsStep2);

        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.True(vm.IsDone);
        Assert.Equal(1, installer.Installs);
        Assert.Equal("2.1 of 5.6 GB", vm.ProgressText);   // the last progress report
        Assert.True(vm.CanGoNext);

        vm.NextCommand.Execute(null);
        Assert.True(vm.IsStep3);
        vm.BackCommand.Execute(null);
        Assert.True(vm.IsStep2);
    }

    [Fact]
    public async Task R9_4_FirstRunShowsAFriendlyErrorAndRetries()
    {
        using var s = new FakeServices();
        var installer = new FakeInstaller { FailWith = new HttpRequestException("secret internal detail") };
        var vm = new FirstRunViewModel(s, installer);
        vm.NextCommand.Execute(null);

        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.True(vm.IsFailed);
        Assert.True(vm.HasMessage);
        Assert.Contains("internet connection", vm.Message);
        Assert.DoesNotContain("secret", vm.Message);
        Assert.False(vm.CanGoNext);

        await vm.DownloadCommand.ExecuteAsync(null);   // Retry
        Assert.True(vm.IsDone);
        Assert.Equal(2, installer.Installs);
    }

    [Fact]
    public void R9_4_FirstRunStartsAtTheDownloadWhenTheModelWentMissing()
    {
        using var s = new FakeServices();
        s.Settings = s.Settings with { FirstRunDone = true };
        var vm = new FirstRunViewModel(s, new FakeInstaller { Installed = false });
        Assert.True(vm.IsStep2);
    }

    [Fact]
    public void R9_4_FirstRunShowsTheShortcutsAsKeys()
    {
        using var s = new FakeServices();
        var vm = new FirstRunViewModel(s, new FakeInstaller { Installed = true });
        var rows = vm.ShortcutRows;
        Assert.Equal(["Ctrl", "Alt", "P"], rows[0].Keys);
        Assert.Equal(["Ctrl", "Alt", "Z"], rows[1].Keys);
        Assert.Equal(["Ctrl", "Z"], rows[2].Keys);
        Assert.Equal(["Tab"], rows[3].Keys);
    }

    [Fact]
    public void R9_4_SizesAreShownLikeWindowsDoes()
    {
        const long Gb = 1024L * 1024 * 1024;
        Assert.Equal("2.1 of 5.6 GB", FirstRunViewModel.FormatSize((long)(2.1 * Gb), (long)(5.6 * Gb)));
        Assert.Equal("0.0 of 0.0 GB", FirstRunViewModel.FormatSize(0, 1));
    }
}
