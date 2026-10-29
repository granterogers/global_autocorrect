using System.Runtime.InteropServices;
using System.Text;
using GlobalAutocorrect.Windows.Uia;

namespace GlobalAutocorrect.Windows.Targets;

/// <summary>Apps we never touch (8.4). The list is editable in settings.</summary>
public sealed class AppExclusions
{
    // games: any process with a full-screen exclusive window is also skipped (see IsFullScreen)
    public static IReadOnlyList<string> Defaults { get; } =
    [
        "WindowsTerminal", "cmd", "powershell", "pwsh", "conhost", "OpenConsole", "wt",
        "Code", "Code - Insiders", "Cursor", "devenv", "rider64", "idea64", "pycharm64", "sublime_text", "notepad++",
        "KeePass", "KeePassXC", "1Password", "Bitwarden", "Dashlane",
        "mstsc", "msrdc", "vmconnect", "RemoteDesktopManager", "AnyDesk", "TeamViewer",
        "steam", "EpicGamesLauncher",
    ];

    private static readonly HashSet<string> CellControlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "data item", "item", "cell", "table", "data grid",
    };

    private readonly HashSet<string> _names;

    public AppExclusions(IEnumerable<string> processNames)
    {
        _names = new HashSet<string>(
            processNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool IsExcludedProcess(string processName) =>
        !string.IsNullOrEmpty(processName) && _names.Contains(processName);

    /// <summary>Excel with a cell/data item focused (8.4). Control type names come from UIA, en-GB.</summary>
    public static bool IsExcelCell(UiaSnapshot s) =>
        string.Equals(s.ProcessName, "EXCEL", StringComparison.OrdinalIgnoreCase)
        && CellControlTypes.Contains(s.ControlType);

    /// <summary>Window rect equals its monitor's rect, excluding the desktop and shell windows.</summary>
    public static bool IsFullScreen(nint hwnd)
    {
        if (hwnd == 0) return false;

        var cls = new StringBuilder(64);
        if (Native.GetClassName(hwnd, cls, cls.Capacity) > 0)
        {
            string name = cls.ToString();
            if (name is "Progman" or "WorkerW") return false;
        }

        if (!Native.GetWindowRect(hwnd, out RECT window)) return false;
        nint monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        if (monitor == 0) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return false;

        RECT m = info.rcMonitor;
        return window.Left == m.Left && window.Top == m.Top && window.Right == m.Right && window.Bottom == m.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

#pragma warning disable SYSLIB1054 // DllImport is fine for these few calls.
    private static class Native
    {
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetClassName(nint hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(nint hwnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern nint MonitorFromWindow(nint hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    }
#pragma warning restore SYSLIB1054
}
