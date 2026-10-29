using System.Windows;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.App.Theme;
using GlobalAutocorrect.App.Views;

namespace GlobalAutocorrect.App;

public partial class App
{
    private SettingsWindow? _settingsWindow;
    private AppHostServices? _services;

    /// <summary>The host as the settings and first-run windows see it (settings, live status).</summary>
    private AppHostServices Services => _services ??= new AppHostServices(Host);

    /// <summary>
    /// Opens the settings window (tray double click, "Settings…" in the tray menu). Only one exists: if it is already
    /// open it is brought to the front instead.
    /// </summary>
    public void OpenSettings()
    {
        if (_settingsWindow is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }

        var window = new SettingsWindow(Services, Services);
        ThemeService.Watch(window);   // Mica backdrop and caption colours follow Windows (9.2.d)
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Show();
        window.Activate();
    }
}
