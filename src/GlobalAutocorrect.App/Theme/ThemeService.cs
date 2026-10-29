using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace GlobalAutocorrect.App.Theme;

/// <summary>
/// Applies the design tokens (9.2.a) and follows the Windows light/dark setting (9.2.d). It applies WPF-UI's theme with our
/// accent (<see cref="Palette.AccentPrimary"/>, never the system accent), then swaps the neutral colours and brushes in
/// <c>Application.Resources</c> to the matching light or dark set, so every <c>DynamicResource</c> updates live.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private readonly Application _app;
    private readonly Dispatcher _dispatcher;
    private bool _applying;
    private bool _started;

    public ThemeService(Application app)
    {
        _app = app;
        _dispatcher = app.Dispatcher;
    }

    public ApplicationTheme Current { get; private set; } = ApplicationTheme.Unknown;

    /// <summary>Raised on the UI thread after the tokens were swapped.</summary>
    public event Action<ApplicationTheme>? ThemeChanged;

    /// <summary>Applies the Windows theme now and keeps following it. Call once, on the UI thread, from OnStartup.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        Apply(IsWindowsDark() ? ApplicationTheme.Dark : ApplicationTheme.Light);
        // No window exists while the app sits in the tray, so listen to the system directly. Windows that do exist also
        // go through Watch(), which keeps their Mica backdrop and title bar in step.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ApplicationThemeManager.Changed += OnWpfUiThemeChanged;
    }

    /// <summary>Applies a theme explicitly: WPF-UI resources, our accent, then our token set.</summary>
    public void Apply(ApplicationTheme theme)
    {
        if (theme is not (ApplicationTheme.Light or ApplicationTheme.Dark)) theme = ApplicationTheme.Light;
        _applying = true;
        try
        {
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, false);   // false: keep our accent, not the system one
            ApplyAccentAndTokens(theme);
        }
        finally { _applying = false; }
        Current = theme;
        ThemeChanged?.Invoke(theme);
    }

    /// <summary>Makes a window follow the Windows theme (Mica backdrop, caption colours) without letting WPF-UI reset the accent.</summary>
    public static void Watch(Window window) => SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, false);

    public static bool IsWindowsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        _dispatcher.BeginInvoke(() =>
        {
            var wanted = IsWindowsDark() ? ApplicationTheme.Dark : ApplicationTheme.Light;
            if (wanted != Current) Apply(wanted);
        });
    }

    // A watched window made WPF-UI switch theme by itself: keep our accent and tokens on top of it.
    private void OnWpfUiThemeChanged(ApplicationTheme theme, Color systemAccent)
    {
        if (_applying || theme is not (ApplicationTheme.Light or ApplicationTheme.Dark)) return;
        _dispatcher.BeginInvoke(() =>
        {
            ApplyAccentAndTokens(theme);
            if (theme != Current)
            {
                Current = theme;
                ThemeChanged?.Invoke(theme);
            }
        });
    }

    private void ApplyAccentAndTokens(ApplicationTheme theme)
    {
        ApplicationAccentColorManager.Apply(Palette.ToColor(Palette.AccentPrimary), theme);
        ApplyTokens(theme == ApplicationTheme.Dark);
    }

    /// <summary>Copies the light or dark neutrals into the un-prefixed token names and rebuilds their brushes.</summary>
    internal void ApplyTokens(bool dark)
    {
        var n = Palette.For(dark);
        var res = _app.Resources;
        SetToken(res, "SurfaceBase", n.SurfaceBase);
        SetToken(res, "SurfaceCard", n.SurfaceCard);
        SetToken(res, "BorderSoft", n.BorderSoft);
        SetToken(res, "TextPrimary", n.TextPrimary);
        SetToken(res, "TextSecondary", n.TextSecondary);

        // WPF-UI's own cards and text read these keys, so its controls use our palette too (9.2.a "defined once").
        res["TextFillColorPrimaryBrush"] = Frozen(n.TextPrimary);
        res["TextFillColorSecondaryBrush"] = Frozen(n.TextSecondary);
        res["CardBackgroundFillColorDefaultBrush"] = Frozen(n.SurfaceCard);
        res["CardStrokeColorDefaultBrush"] = Frozen(n.BorderSoft);
    }

    private static void SetToken(ResourceDictionary res, string name, string hex)
    {
        res[name] = Palette.ToColor(hex);
        res[name + "Brush"] = Frozen(hex);
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var b = new SolidColorBrush(Palette.ToColor(hex));
        b.Freeze();
        return b;
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        ApplicationThemeManager.Changed -= OnWpfUiThemeChanged;
    }
}
