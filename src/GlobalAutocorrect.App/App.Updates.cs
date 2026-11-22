using System.Net.Http;
using GlobalAutocorrect.App.Updates;
using GlobalAutocorrect.App.ViewModels;
using GlobalAutocorrect.Core.Updates;

namespace GlobalAutocorrect.App;

public partial class App
{
    private UpdateMonitor? _updates;
    private HttpClient? _updateHttp;

    /// <summary>
    /// D58: the update check, while "Check for updates" is on. Never under the screen tests (GA_E2E), and not for a
    /// build whose version can't be read.
    /// </summary>
    /// <summary>D131: how long since the last keyboard or mouse input anywhere on the PC.</summary>
    private static TimeSpan IdleFor()
    {
        var info = new GlobalAutocorrect.Windows.Native.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<GlobalAutocorrect.Windows.Native.LASTINPUTINFO>() };
        if (!GlobalAutocorrect.Windows.Native.NativeMethods.GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return GlobalAutocorrect.Core.Health.IdleTime.Since(info.dwTime, unchecked((uint)Environment.TickCount));   // D153
    }

    private void StartUpdateChecks()
    {
        if (Environment.GetEnvironmentVariable("GA_E2E") == "1") return;
        if (!ReleaseVersion.TryParse(AboutViewModel.InstalledVersion, out var installed)) return;
        UpdateInstaller.CleanOldWorkFolders();
        _updateHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // each call sets its own limit
        var client = new UpdateClient(_updateHttp);
        _updates = new UpdateMonitor(
            Services,
            ct => client.CheckAsync(installed, ct),
            (u, ct) => client.DownloadAsync(u, UpdateInstaller.NewWorkFolder(), ct),
            UpdateInstaller.Run,
            Host.ShowNotice,
            a => Dispatcher.BeginInvoke(a),
            IdleFor,                         // D131
            UpdateInstaller.RunQuietly);
        _updates.Start();
        // D174: locking the PC (Win+L, or the screen locking itself) means nobody is typing: install a found update then.
        Microsoft.Win32.SessionSwitchEventHandler onLock = (_, e) =>
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock) _ = Task.Run(_updates.InstallOnLockAsync);
        };
        Microsoft.Win32.SystemEvents.SessionSwitch += onLock;
        Exit += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.SessionSwitch -= onLock;
            _updates.Dispose();
            _updateHttp.Dispose();
        };
    }
}
