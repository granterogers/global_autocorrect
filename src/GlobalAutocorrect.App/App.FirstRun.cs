using System.Windows;
using GlobalAutocorrect.App.Models;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.App.Theme;
using GlobalAutocorrect.App.ViewModels;
using GlobalAutocorrect.App.Views;

namespace GlobalAutocorrect.App;

public partial class App
{
    private FirstRunWindow? _firstRunWindow;

    /// <summary>
    /// Shows the first-run window (9.4) when set-up hasn't been finished or the model isn't on this PC. Called from
    /// <c>OnHostStartedPartial</c> in App.Tray.cs, once the theme and tray exist.
    /// </summary>
    private void ShowFirstRunIfNeeded()
    {
        // E2E runs drive the real desktop with a fresh settings folder; a welcome window would only get in the way.
        if (Environment.GetEnvironmentVariable("GA_E2E") == "1") return;

        var installer = new ModelInstaller(new ModelDownloader(AppHost.AssetsDir, AppHost.CacheDir));
        if (Host.Settings.FirstRunDone && installer.IsInstalled(Host.Settings.Model)) return;

        var window = new FirstRunWindow(new FirstRunViewModel(Services, installer));
        ThemeService.Watch(window);
        window.Closed += (_, _) => _firstRunWindow = null;
        _firstRunWindow = window;
        window.Show();
        window.Activate();
    }
}
