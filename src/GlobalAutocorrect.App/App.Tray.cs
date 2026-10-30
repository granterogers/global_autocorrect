using System.IO;
using System.Windows;
using GlobalAutocorrect.App.Theme;
using GlobalAutocorrect.App.Tray;

namespace GlobalAutocorrect.App;

public partial class App
{
    private ThemeService? _theme;
    private TrayController? _tray;

    public ThemeService Theme => _theme ?? throw new InvalidOperationException("The app has not started yet.");

    /// <summary>The settings window (T9.3) subscribes to this.</summary>
    public TrayController Tray => _tray ?? throw new InvalidOperationException("The app has not started yet.");

    partial void OnHostStartedPartial()
    {
        // Colours first: the tray menu and every later window read the token brushes.
        _theme = new ThemeService(this);
        _theme.Start();

        // 8.1: start with Windows (setting, on by default).
        new StartupRegistration().Apply(Host.Settings.StartWithWindows);

        _tray = new TrayController(Host, Path.Combine(AppHost.AssetsDir, "icons"));
        _tray.Start();
        _tray.OpenSettingsRequested += OpenSettings;   // T9.3: double click and the "Settings…" menu item
        Exit += (_, _) =>
        {
            _tray?.Dispose();
            _theme?.Dispose();
        };

        ShowFirstRunIfNeeded();   // T9.4
        StartUpdateChecks();      // D58
    }
}
