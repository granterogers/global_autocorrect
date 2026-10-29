using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlobalAutocorrect.Windows.Overlay;

/// <summary>
/// Base for the suggestion and underline overlays (7.6, 10.3.d): transparent, click-through, never takes focus,
/// always on top, not in Alt+Tab. Positioned in physical pixels, which is what UIA reports.
/// </summary>
public class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
    private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_NOZORDER = 0x4;
    private static readonly nint HWND_TOPMOST = -1;

    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(nint h, int i, int v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);

    protected nint Handle { get; private set; }

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.Manual;
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLongW(Handle, GWL_EXSTYLE);
            SetWindowLongW(Handle, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOPMOST);
        };
    }

    /// <summary>Moves and sizes the window in physical screen pixels, without activating it.</summary>
    protected void PlaceAt(double x, double y, double width, double height)
    {
        if (Handle == 0) { Show(); Hide(); }   // create the HWND
        SetWindowPos(Handle, HWND_TOPMOST, (int)Math.Round(x), (int)Math.Round(y), Math.Max(1, (int)Math.Ceiling(width)), Math.Max(1, (int)Math.Ceiling(height)), SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>DIPs per physical pixel on this window's monitor.</summary>
    protected double PixelsToDips => 1.0 / VisualTreeHelper.GetDpi(this).DpiScaleX;
}
