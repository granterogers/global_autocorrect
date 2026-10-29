using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GlobalAutocorrect.App.Tray;

/// <summary>
/// A notification-area icon on plain Shell_NotifyIcon (version 4 behaviour): icon, tooltip, balloon, and clean left-click,
/// double-click and context-menu events. WPF-UI.Tray's NotifyIcon has no balloon support, so the tray uses this instead.
/// Create and use on the UI thread.
/// </summary>
internal sealed class NativeTrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8001;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NIIF_INFO = 0x01, NIIF_NOSOUND = 0x10;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint IconId = 1;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly DispatcherTimer _singleClickTimer = new();
    private HwndSource? _source;
    private IntPtr _icon;
    private string _tooltip = "";
    private bool _added;
    private long _lastDoubleTick = long.MinValue;

    public NativeTrayIcon()
    {
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            LeftClick?.Invoke();
        };
    }

    /// <summary>A single left click (delayed by the system double-click time so a double click doesn't also fire it).</summary>
    public event Action? LeftClick;
    public event Action? LeftDoubleClick;
    /// <summary>Right click or the keyboard menu key on the icon.</summary>
    public event Action? ContextMenuRequested;

    public void Show(IntPtr icon, string tooltip)
    {
        _icon = icon;
        _tooltip = tooltip;
        _source ??= CreateMessageWindow();
        _added = Notify(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (_added) Notify(NIM_SETVERSION, 0, version: NOTIFYICON_VERSION_4);
    }

    public void Update(IntPtr icon, string tooltip)
    {
        _icon = icon;
        _tooltip = tooltip;
        if (_added) Notify(NIM_MODIFY, NIF_ICON | NIF_TIP | NIF_SHOWTIP);
    }

    /// <summary>Balloon / toast next to the tray icon. Only fixed, plain-language app messages go through here (8.6).</summary>
    public void ShowBalloon(string title, string message)
    {
        if (!_added) return;
        Notify(NIM_MODIFY, NIF_INFO, title, message);
    }

    private HwndSource CreateMessageWindow()
    {
        var p = new HwndSourceParameters("GlobalAutocorrect.Tray") { ParentWindow = HWND_MESSAGE, Width = 0, Height = 0 };
        var source = new HwndSource(p);
        source.AddHook(WndProc);
        return source;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated && _added)
        {
            // Explorer restarted: its notification area is empty again.
            Notify(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
            Notify(NIM_SETVERSION, 0, version: NOTIFYICON_VERSION_4);
            handled = true;
        }
        else if (msg == WM_APP_TRAY)
        {
            int evt = (int)(lParam.ToInt64() & 0xFFFF);
            switch (evt)
            {
                case NIN_SELECT: OnSelect(); break;
                case NIN_KEYSELECT: LeftClick?.Invoke(); break;
                case WM_LBUTTONDBLCLK: OnDoubleClick(); break;
                case WM_CONTEXTMENU: ContextMenuRequested?.Invoke(); break;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnSelect()
    {
        long now = Environment.TickCount64;
        if (now - _lastDoubleTick < GetDoubleClickTime()) return;   // second click-up of a double click, already handled
        if (_singleClickTimer.IsEnabled) { OnDoubleClick(); return; }
        _singleClickTimer.Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime());
        _singleClickTimer.Start();
    }

    private void OnDoubleClick()
    {
        long now = Environment.TickCount64;
        if (now - _lastDoubleTick < GetDoubleClickTime()) return;
        _lastDoubleTick = now;
        _singleClickTimer.Stop();
        LeftDoubleClick?.Invoke();
    }

    private bool Notify(uint message, uint flags, string? infoTitle = null, string? info = null, uint version = 0)
    {
        if (_source is null) return false;
        var data = new NOTIFYICONDATA
        {
            hWnd = _source.Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _icon,
            szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
            uVersion = version,
            szInfoTitle = infoTitle is null ? "" : (infoTitle.Length > 63 ? infoTitle[..63] : infoTitle),
            szInfo = info is null ? "" : (info.Length > 255 ? info[..255] : info),
            dwInfoFlags = info is null ? 0 : NIIF_INFO | NIIF_NOSOUND,
        };
        data.cbSize = Marshal.SizeOf(data);
        return Shell_NotifyIcon(message, ref data);
    }

    public void Dispose()
    {
        _singleClickTimer.Stop();
        if (_added) Notify(NIM_DELETE, 0);
        _added = false;
        _source?.Dispose();
        _source = null;
    }

#pragma warning disable SYSLIB1054 // DllImport is fine for these few calls.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;   // union with uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
#pragma warning restore SYSLIB1054
}
