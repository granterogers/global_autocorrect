using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Path = System.IO.Path;

namespace GlobalAutocorrect.E2E.Support;

/// <summary>
/// Pixels on screen, for checks that can only be made by looking (7.6: the suggestion's size). Everything is in physical
/// screen pixels: callers wrap their work in <see cref="PerMonitorDpi"/> so UIA, window rectangles and captures agree.
/// </summary>
public static class Screen
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, nint lParam);
    private delegate bool EnumProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint ctx);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;

    /// <summary>For the duration: this thread sees physical pixels, as UIA reports them and the app places its overlay.</summary>
    public static IDisposable PerMonitorDpi()
    {
        nint old = SetThreadDpiAwarenessContext(-4);   // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        return new Undo(() => { if (old != 0) SetThreadDpiAwarenessContext(old); });
    }

    private sealed class Undo(Action a) : IDisposable { public void Dispose() => a(); }

    /// <summary>
    /// The app's visible overlay windows (7.6: transparent, click-through, no-activate) that are at least
    /// <paramref name="minHeight"/> tall: the suggestion, not the thin correction underline.
    /// </summary>
    public static List<(nint Hwnd, Rectangle Rect)> Overlays(int pid, int minHeight = 6)
    {
        var found = new List<(nint, Rectangle)>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p != pid || !IsWindowVisible(h)) return true;
            int ex = GetWindowLongW(h, GWL_EXSTYLE);
            const int need = WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            if ((ex & need) != need) return true;
            GetWindowRect(h, out var r);
            if (r.Bottom - r.Top >= minHeight) found.Add((h, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)));
            return true;
        }, 0);
        return found;
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    /// <summary>The whole desktop (all monitors), in this thread's pixels.</summary>
    public static Rectangle Bounds => new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    public static void Hide(nint hwnd) => ShowWindowAsync(hwnd, 0);   // SW_HIDE

    /// <summary>
    /// The focused field's caret, measured as UIA reports it (the same way the app does): the TextPattern2 caret range,
    /// else the collapsed selection; its own rectangle, or else the right edge of the character before it, with that
    /// character's height. <paramref name="source"/> says which.
    /// </summary>
    public static Rectangle? CaretRect(UIA3Automation uia, out string source)
    {
        source = "none";
        try
        {
            var el = uia.FocusedElement();
            if (el is null || !el.Patterns.Text.IsSupported) return null;
            FlaUI.Core.ITextRange caret;
            if (el.Patterns.Text2.IsSupported) { caret = el.Patterns.Text2.Pattern.GetCaretRange(out _); source = "TextPattern2 caret range"; }
            else
            {
                var sel = el.Patterns.Text.Pattern.GetSelection();
                if (sel is not { Length: 1 }) return null;
                caret = sel[0];
                source = "TextPattern selection";
            }
            var rects = caret.GetBoundingRectangles();
            if (rects is { Length: > 0 } && rects[0].Height > 0) return rects[0];
            var one = caret.Clone();
            one.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1);
            rects = one.GetBoundingRectangles();
            if (rects is not { Length: > 0 }) return null;
            var c = rects[^1];
            source += ", character before it";
            return new Rectangle(c.Right, c.Top, 0, c.Height);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException or NotSupportedException) { return null; }
    }

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref POINT p);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize, flags;
        public nint hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    /// <summary>The system caret of the foreground window's thread (what the app draws as its blinking caret), if it has one.</summary>
    public static Rectangle? SystemCaret()
    {
        uint thread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.hwndCaret == 0) return null;
        var p = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
        ClientToScreen(info.hwndCaret, ref p);
        return new Rectangle(p.X, p.Y, info.rcCaret.Right - info.rcCaret.Left, info.rcCaret.Bottom - info.rcCaret.Top);
    }

    /// <summary>The focused element's font name as UIA reports it for the character before the caret, or "".</summary>
    public static string FontName(UIA3Automation uia)
    {
        try
        {
            var el = uia.FocusedElement();
            var r = el.Patterns.Text.Pattern.GetSelection()[0].Clone();
            r.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1);
            return r.GetAttributeValue(uia.TextAttributeLibrary.FontName) as string ?? "";
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException or NotSupportedException or IndexOutOfRangeException) { return ""; }
    }

    /// <summary>A screen capture that includes layered windows (CAPTUREBLT), which the overlays are.</summary>
    public static Bitmap Capture(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        nint screen = GetDC(0);
        nint dst = g.GetHdc();
        try { BitBlt(dst, 0, 0, r.Width, r.Height, screen, r.Left, r.Top, 0x00CC0020 | 0x40000000); }   // SRCCOPY | CAPTUREBLT
        finally { g.ReleaseHdc(dst); ReleaseDC(0, screen); }
        return bmp;
    }

    public static void Save(Bitmap bmp, string name)
    {
        string dir = Path.Combine(Desk.Repo, "artifacts", "screens");
        Directory.CreateDirectory(dir);
        bmp.Save(Path.Combine(dir, name), ImageFormat.Png);
    }

    private static double Lum(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    /// <summary>
    /// Ink rows of dark text in <paramref name="area"/> (relative to <paramref name="bmp"/>): rows holding a pixel at
    /// least half as dark against the area's background as the darkest one. ClearType colours the edges of thin stems,
    /// so colour counts; only a bright red spell-check squiggle is left out. Returns (top, bottom) inclusive, or null.
    /// </summary>
    public static (int Top, int Bottom)? TextInk(Bitmap bmp, Rectangle area)
    {
        area.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
        if (area.Width <= 0 || area.Height <= 0) return null;
        var lums = new List<double>();
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++) lums.Add(Lum(bmp.GetPixel(x, y)));
        lums.Sort();
        double bg = lums[lums.Count / 2];   // most of a line's area is background
        return Rows(area, (x, y) =>
        {
            var c = bmp.GetPixel(x, y);
            bool squiggle = c.R > 150 && c.G < 90 && c.B < 90;
            return squiggle ? 0 : Math.Max(0, bg - Lum(c));
        });
    }

    /// <summary>Ink rows of what differs between two captures of the same area (an overlay shown, then hidden).</summary>
    public static (int Top, int Bottom)? DiffInk(Bitmap with, Bitmap without, Rectangle area)
    {
        area.Intersect(new Rectangle(0, 0, Math.Min(with.Width, without.Width), Math.Min(with.Height, without.Height)));
        if (area.Width <= 0 || area.Height <= 0) return null;
        return Rows(area, (x, y) => Math.Abs(Lum(with.GetPixel(x, y)) - Lum(without.GetPixel(x, y))));
    }

    private static (int, int)? Rows(Rectangle area, Func<int, int, double> ink)
    {
        double max = 0;
        var v = new double[area.Height, area.Width];
        for (int y = 0; y < area.Height; y++)
            for (int x = 0; x < area.Width; x++) max = Math.Max(max, v[y, x] = ink(area.Left + x, area.Top + y));
        if (max < 20) return null;   // nothing that reads as text
        int top = -1, bottom = -1;
        for (int y = 0; y < area.Height; y++)
            for (int x = 0; x < area.Width; x++)
                if (v[y, x] >= max / 2) { if (top < 0) top = y; bottom = y; break; }
        return (area.Top + top, area.Top + bottom);
    }
}
