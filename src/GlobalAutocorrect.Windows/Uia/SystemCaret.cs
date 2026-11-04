using System.Runtime.InteropServices;

namespace GlobalAutocorrect.Windows.Uia;

/// <summary>
/// D85: the caret Windows itself knows about (GetGUIThreadInfo), which Win32 and WinUI text boxes keep up to date.
/// Measured on the user's PC at 125% scaling: Notepad's UIA caret range was 42 px tall (the line above included) where
/// its real caret was 22 px, so the suggestion was drawn about 1.9 times too big and too high (user report, "20 to 30%
/// larger" in every app). Chromium apps have no system caret; their UIA caret is used as before.
/// </summary>
public static class SystemCaret
{
    /// <summary>
    /// The caret to size and place the suggestion by: the system caret when it is the same caret UIA reports (same left
    /// edge within 4 px, inside UIA's box) but shorter; otherwise UIA's. Rectangles are physical pixels (x, y, w, h).
    /// </summary>
    public static (double X, double Y, double W, double H)? Pick((double X, double Y, double W, double H)? uia, (double X, double Y, double W, double H)? system)
    {
        if (uia is not { } u) return system;
        if (system is not { } s || s.H <= 0) return u;
        bool sameCaret = Math.Abs(s.X - u.X) <= 4 && s.Y >= u.Y - 2 && s.Y + s.H <= u.Y + u.H + 2;
        return sameCaret && s.H < u.H * 0.85 ? (u.X, s.Y, u.W, s.H) : u;
    }

    /// <summary>The system caret of the foreground window's thread, if that window belongs to <paramref name="processId"/>.</summary>
    public static (double X, double Y, double W, double H)? Read(int processId)
    {
        try
        {
            nint fg = GetForegroundWindow();
            uint thread = GetWindowThreadProcessId(fg, out uint pid);
            if (fg == 0 || pid != (uint)processId) return null;
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(thread, ref info) || info.hwndCaret == 0) return null;
            var p = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
            if (!ClientToScreen(info.hwndCaret, ref p)) return null;
            int h = info.rcCaret.Bottom - info.rcCaret.Top;
            return h > 0 ? (p.X, p.Y, Math.Max(1, info.rcCaret.Right - info.rcCaret.Left), h) : null;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return null; }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref POINT p);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize, flags;
        public nint hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }
}
