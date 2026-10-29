using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Window = System.Windows.Window;

namespace GlobalAutocorrect.App.Tray;

/// <summary>
/// The tray icon (8.1, 8.2): tooltip and icon follow the paused state, left click toggles pause, double click opens
/// Settings, the right-click menu is a WPF-UI styled context menu, and <see cref="AppHost.Notice"/> messages appear as a
/// balloon. Everything runs on the UI thread; host events are marshalled onto it.
/// </summary>
public sealed class TrayController : IDisposable
{
    public const string AppName = "Global Autocorrect";

    private readonly AppHost _host;
    private readonly string _iconDir;
    private readonly Dispatcher _dispatcher;
    private readonly NativeTrayIcon _icon = new();
    private IntPtr _activeIcon, _pausedIcon;
    private ContextMenu? _menu;
    private Wpf.Ui.Controls.MenuItem? _pauseItem;
    private Window? _menuOwner;
    private bool _disposed;

    /// <param name="iconDir">Folder holding tray-active.ico and tray-paused.ico (assets/icons next to the exe).</param>
    public TrayController(AppHost host, string iconDir)
    {
        _host = host;
        _iconDir = iconDir;
        _dispatcher = Application.Current.Dispatcher;
    }

    /// <summary>Double click on the tray icon, or "Settings…" in its menu. The settings window subscribes (T9.3).</summary>
    public event Action? OpenSettingsRequested;

    /// <summary>The tooltip for the current state (8.2).</summary>
    public static string TooltipFor(bool paused) => paused ? $"{AppName} — paused" : $"{AppName} — on";

    /// <summary>Adds the icon to the notification area. Call once on the UI thread.</summary>
    public void Start()
    {
        int px = TrayIconPixels();
        _activeIcon = LoadIcon(Path.Combine(_iconDir, "tray-active.ico"), px);
        _pausedIcon = LoadIcon(Path.Combine(_iconDir, "tray-paused.ico"), px);

        _icon.LeftClick += () => _host.SetPaused(!_host.Paused);     // 8.2: one click
        _icon.LeftDoubleClick += RaiseOpenSettings;
        _icon.ContextMenuRequested += ShowMenu;
        _icon.Show(IconFor(_host.Paused), TooltipFor(_host.Paused));

        _host.StateChanged += OnStateChanged;
        _host.Notice += OnNotice;
    }

    private void RaiseOpenSettings() => OpenSettingsRequested?.Invoke();

    private void OnStateChanged() => _dispatcher.BeginInvoke(Refresh);

    private void OnNotice(string message) => _dispatcher.BeginInvoke(() =>
    {
        if (!_disposed) _icon.ShowBalloon(AppName, message);
    });

    private void Refresh()
    {
        if (_disposed) return;
        _icon.Update(IconFor(_host.Paused), TooltipFor(_host.Paused));
        UpdatePauseItem();
    }

    private IntPtr IconFor(bool paused) => paused ? _pausedIcon : _activeIcon;

    // ---- Context menu ---------------------------------------------------------------------------------------------

    private void ShowMenu()
    {
        if (_disposed) return;
        _menu ??= BuildMenu();
        UpdatePauseItem();

        // A tray icon owns no window, and a popup only closes on a click elsewhere if some window of ours is the
        // foreground window. Use an invisible 1x1 window for that.
        _menuOwner ??= new Window
        {
            Width = 1, Height = 1, Left = 0, Top = 0,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            Opacity = 0, ShowInTaskbar = false, ShowActivated = true, ResizeMode = ResizeMode.NoResize,
        };
        _menuOwner.Show();
        var hwnd = new WindowInteropHelper(_menuOwner).Handle;
        SetForegroundWindow(hwnd);

        _menu.PlacementTarget = _menuOwner;
        _menu.Placement = PlacementMode.Mouse;
        _menu.StaysOpen = false;
        _menu.IsOpen = true;
    }

    private ContextMenu BuildMenu()
    {
        _pauseItem = new Wpf.Ui.Controls.MenuItem();
        _pauseItem.Click += (_, _) => _host.SetPaused(!_host.Paused);

        var settings = new Wpf.Ui.Controls.MenuItem { Header = "Settings…", Icon = new SymbolIcon(SymbolRegular.Settings24) };
        settings.Click += (_, _) => RaiseOpenSettings();

        var quit = new Wpf.Ui.Controls.MenuItem { Header = "Quit", Icon = new SymbolIcon(SymbolRegular.Power24) };
        quit.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu { MinWidth = 200 };
        menu.Items.Add(_pauseItem);
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        menu.Closed += (_, _) => _menuOwner?.Hide();
        return menu;
    }

    /// <summary>"Pause" while running, "Resume" while paused, with a matching icon.</summary>
    private void UpdatePauseItem()
    {
        if (_pauseItem is null) return;
        bool paused = _host.Paused;
        _pauseItem.Header = paused ? "Resume" : "Pause";
        _pauseItem.Icon = new SymbolIcon(paused ? SymbolRegular.Play24 : SymbolRegular.Pause24);
    }

    // ---- Icons ----------------------------------------------------------------------------------------------------

    /// <summary>The notification area's icon size at the current DPI (16 px at 100 %).</summary>
    private static int TrayIconPixels()
    {
        const int SM_CXSMICON = 49;
        uint dpi = GetDpiForSystem();
        int px = dpi == 0 ? 16 : GetSystemMetricsForDpi(SM_CXSMICON, dpi);
        return px > 0 ? px : 16;
    }

    private static IntPtr LoadIcon(string path, int px)
    {
        const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
        return File.Exists(path) ? LoadImage(IntPtr.Zero, path, IMAGE_ICON, px, px, LR_LOADFROMFILE) : IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.StateChanged -= OnStateChanged;
        _host.Notice -= OnNotice;
        if (_menu is not null) _menu.IsOpen = false;
        _icon.Dispose();
        _menuOwner?.Close();
        if (_activeIcon != IntPtr.Zero) DestroyIcon(_activeIcon);
        if (_pausedIcon != IntPtr.Zero) DestroyIcon(_pausedIcon);
    }

#pragma warning disable SYSLIB1054 // DllImport is fine for these few calls.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
#pragma warning restore SYSLIB1054
}
