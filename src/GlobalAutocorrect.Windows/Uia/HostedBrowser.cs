using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace GlobalAutocorrect.Windows.Uia;

/// <summary>
/// D87, user report "WhatsApp still not working": a WinUI 3 app can host its web content (WebView2) so that UIA's focus
/// stops at the host frame, a pane with no text. The browser engine, a child process of the app, still has a window of
/// its own with the whole accessibility tree, in real screen coordinates, and in it the element that has the keyboard
/// focus. When the focused element has no text, that element is read instead. No app is named: this is any app that
/// hosts a Chromium engine this way.
/// </summary>
public sealed class HostedBrowser
{
    private const int HasKeyboardFocusPropertyId = 30008;   // UIA_HasKeyboardFocusPropertyId
    private static readonly TimeSpan WindowsFor = TimeSpan.FromSeconds(5);

    private int _hostPid;
    private nint _foreground;
    private DateTime _windowsAt;
    private nint[] _windows = [];
    private IUIAutomationElement? _last;

    /// <summary>
    /// The element with the keyboard focus inside a browser engine that <paramref name="hostPid"/> hosts, or null.
    /// The engine keeps its own focus while the app is in the background, so it is only used when the window the user
    /// is typing in is the host's and has the engine's input window inside it. An app that merely started a browser or
    /// an app hidden in the tray (Explorer starting one, say) has no such child, and is never read through.
    /// </summary>
    public IUIAutomationElement? FocusedIn(IUIAutomation2 uia, int hostPid) => FocusedIn(uia, hostPid, GetForegroundWindow());

    /// <summary>As if <paramref name="fg"/> were the window being typed in (the tests' way in).</summary>
    public IUIAutomationElement? FocusedIn(IUIAutomation2 uia, int hostPid, nint fg)
    {
        if (hostPid != _hostPid || fg != _foreground || DateTime.UtcNow - _windowsAt > WindowsFor)
        {
            if (hostPid != _hostPid || fg != _foreground) _last = null;
            _hostPid = hostPid;
            _foreground = fg;
            _windowsAt = DateTime.UtcNow;
            _windows = HostsBrowserInside(fg, hostPid) ? BrowserWindows(hostPid) : [];
        }
        if (_windows.Length == 0) return null;
        // The same box as last time: one cheap call, instead of searching the page.
        if (_last is not null)
        {
            try { if (_last.CurrentHasKeyboardFocus != 0) return _last; }
            catch (COMException) { }
            _last = null;
        }
        var focused = uia.CreatePropertyCondition(HasKeyboardFocusPropertyId, true);
        foreach (var hwnd in _windows)
        {
            var found = uia.ElementFromHandle(hwnd)?.FindFirst(TreeScope.TreeScope_Descendants, focused);
            if (found is not null && Inside(found.CurrentBoundingRectangle, fg)) return _last = found;   // drawn in the window typed in
        }
        return null;
    }

    private static bool Inside(tagRECT r, nint window)
    {
        if (!GetWindowRect(window, out var w) || r.right <= r.left) return false;
        int cx = (r.left + r.right) / 2, cy = (r.top + r.bottom) / 2;
        return cx >= w.left && cx <= w.right && cy >= w.top && cy <= w.bottom;
    }

    /// <summary>The host's own window with the engine's input window (a Chromium-class child window) inside it.</summary>
    public static bool HostsBrowserInside(nint window, int hostPid)
    {
        if (window == 0) return false;
        GetWindowThreadProcessId(window, out uint pid);
        if (pid != hostPid) return false;
        bool inside = false;
        EnumChildWindows(window, (h, _) => !(inside = IsBrowserWindow(ClassOf(h))), 0);
        return inside;
    }

    /// <summary>The host's child processes' Chromium windows that aren't shown: the hosted engine's, drawn by the host.</summary>
    public static nint[] BrowserWindows(int hostPid)
    {
        var children = ChildProcesses(hostPid);
        if (children.Count == 0) return [];
        var found = new List<nint>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (children.Contains((int)pid) && !IsWindowVisible(h) && IsBrowserWindow(ClassOf(h))) found.Add(h);
            return true;
        }, 0);
        return [.. found];
    }

    /// <summary>The Chromium engine's top-level window class ("Chrome_WidgetWin_1"); not an app's name.</summary>
    public static bool IsBrowserWindow(string className) => className.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal);

    private static HashSet<int> ChildProcesses(int parent)
    {
        var set = new HashSet<int>();
        nint snap = CreateToolhelp32Snapshot(0x2, 0);   // TH32CS_SNAPPROCESS
        if (snap == -1) return set;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                if (e.th32ParentProcessID == parent) set.Add((int)e.th32ProcessID);
        }
        finally { CloseHandle(snap); }
        return set;
    }

    private static string ClassOf(nint h)
    {
        var buf = new char[64];
        int n = GetClassNameW(h, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc f, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowsProc f, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out tagRECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, char[] name, int max);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(nint snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(nint snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);
}
