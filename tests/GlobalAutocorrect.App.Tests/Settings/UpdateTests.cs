using System.IO;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.App.Tray;
using GlobalAutocorrect.App.Updates;
using GlobalAutocorrect.App.ViewModels;
using GlobalAutocorrect.Core.Settings;
using GlobalAutocorrect.Core.Updates;
using Xunit;

namespace GlobalAutocorrect.App.Tests.Settings;

/// <summary>D58: the About page's update rows and the background check, with GitHub faked.</summary>
public class UpdateTests
{
    private const string Base = "https://github.com/granterogers/global_autocorrect/releases/download/v0.9.0-rc2/";

    internal static AvailableUpdate Rc2 { get; } = new(ReleaseVersion.Parse("0.9.0-rc2"), "v0.9.0-rc2",
        new ReleaseAsset("GlobalAutocorrect-0.9.0-rc2.zip", Base + "GlobalAutocorrect-0.9.0-rc2.zip", 31_000_000),
        new ReleaseAsset("SHA256SUMS.txt", Base + "SHA256SUMS.txt", 100));

    /// <summary>An update service that does what the test says and counts what it was asked.</summary>
    internal sealed class FakeUpdates : IUpdateService
    {
        public AvailableUpdate? Available { get; set; }
        public bool Installing { get; set; }
        public string? Refuse { get; set; }
        public int Installs { get; private set; }
        public int Checks { get; private set; }
        public event Action? Changed;

        public void CheckNow() => Checks++;

        public Task<string?> InstallAsync()
        {
            Installs++;
            return Task.FromResult(Refuse);
        }

        public void Raise() => Changed?.Invoke();
    }

    private static AboutViewModel About(FakeServices s, IUpdateService? updates) =>
        new(s, Path.Combine(s.Dir, "metrics.json"), Path.Combine(s.Dir, "NOTICE.md"),
            new StartupRegistration(@"Software\GlobalAutocorrectTests\Updates", @"C:\App\x.exe"), _ => { }, updates);

    [Fact]
    public void R9_3_g_CheckForUpdatesIsOnByDefaultAndSaved_D58()
    {
        Assert.True(new AppSettings().CheckForUpdates);
        using var s = new FakeServices();
        var updates = new FakeUpdates();
        var vm = About(s, updates);
        Assert.True(vm.CheckForUpdates);

        vm.CheckForUpdates = false;
        Assert.False(s.Settings.CheckForUpdates);
        Assert.Equal(0, updates.Checks);
        vm.CheckForUpdates = true;   // turned back on: checks straight away
        Assert.True(s.Settings.CheckForUpdates);
        Assert.Equal(1, updates.Checks);

        // An older settings file without the switch loads with it on.
        var path = Path.Combine(s.Dir, "settings.json");
        File.WriteAllText(path, """{"PredictionEnabled": true}""");
        Assert.True(new SettingsStore(path).Load().CheckForUpdates);
    }

    [Fact]
    public async Task R13_4_AnUpdateIsShownAndInstalledOnlyWhenTheUserClicks_D58()
    {
        using var s = new FakeServices();
        var updates = new FakeUpdates();
        var vm = About(s, updates);
        Assert.False(vm.HasUpdate);   // nothing found yet: nothing shown

        updates.Available = Rc2;
        vm.Refresh();
        Assert.True(vm.HasUpdate);
        Assert.Equal("Update available: v0.9.0-rc2", vm.UpdateTitle);
        Assert.Contains("about 30 MB", vm.UpdateCaption);
        Assert.Equal(0, updates.Installs);   // showing it never installs it

        updates.Refuse = "The download doesn't match its published checksum, so it was deleted and nothing was installed.";
        await vm.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Equal(1, updates.Installs);
        Assert.Equal(updates.Refuse, vm.UpdateMessage);

        updates.Refuse = null;
        await vm.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Equal(2, updates.Installs);
        Assert.Contains("close and start again", vm.UpdateMessage);

        // Switched off: the update is no longer shown.
        vm.CheckForUpdates = false;
        Assert.False(vm.HasUpdate);
        // No update service (it isn't running): never shown.
        Assert.False(About(s, null).HasUpdate);
    }

    private static UpdateMonitor Monitor(FakeServices s, Func<AvailableUpdate?> found, List<string> notices, List<string>? ran = null,
        Func<AvailableUpdate, string>? download = null, Action? onCheck = null) =>
        new(s,
            _ => { onCheck?.Invoke(); return Task.FromResult(found()); },
            (u, _) => Task.FromResult(download?.Invoke(u) ?? "C:\\temp\\update"),
            f => ran?.Add(f),
            notices.Add);

    [Fact]
    public async Task R13_3_TheTrayAnnouncesEachNewVersionOnce_D58()
    {
        using var s = new FakeServices();
        var notices = new List<string>();
        AvailableUpdate? next = null;
        int checks = 0;
        using var m = Monitor(s, () => next, notices, onCheck: () => checks++);

        await m.CheckAsync();                       // nothing newer: nothing shown or said
        Assert.Null(m.Available);
        Assert.Empty(notices);

        next = Rc2;
        await m.CheckAsync();
        await m.CheckAsync();
        Assert.Same(Rc2, m.Available);
        Assert.Equal(["Global Autocorrect 0.9.0-rc2 is available. Open Settings › About to install it."], notices);
        Assert.Equal("0.9.0-rc2", s.Settings.UpdateNotifiedVersion);   // and not again after a restart
        using (var again = Monitor(s, () => Rc2, notices)) await again.CheckAsync();
        Assert.Single(notices);

        next = null;                               // offline later: what was found stays
        await m.CheckAsync();
        Assert.Same(Rc2, m.Available);

        s.Update(s.Settings with { CheckForUpdates = false });
        int before = checks;
        await m.CheckAsync();
        Assert.Equal(before, checks);              // switched off: GitHub isn't asked
    }

    [Fact]
    public async Task R13_4_InstallingRunsTheCheckedInstallerOrSaysWhyNot_D58()
    {
        using var s = new FakeServices();
        var notices = new List<string>();
        var ran = new List<string>();
        string? refuse = null;
        using var m = Monitor(s, () => Rc2, notices, ran, _ => refuse is null ? @"C:\temp\update\package" : throw new UpdateRefusedException(refuse));

        Assert.Equal("There's no update to install.", await m.InstallAsync());   // before any check
        Assert.Empty(ran);

        await m.CheckAsync();
        refuse = "This release has no checksum file, so the download can't be checked. Nothing was installed.";
        Assert.Equal(refuse, await m.InstallAsync());
        Assert.Empty(ran);                          // refused: the installer never runs
        Assert.False(m.Installing);

        refuse = null;
        Assert.Null(await m.InstallAsync());
        Assert.Equal([@"C:\temp\update\package"], ran);
    }
}
