using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace GlobalAutocorrect.Windows.Hotkeys;

/// <summary>
/// Owns a dedicated thread with a message-only window and a message loop, and registers
/// global hotkeys on it (10.3.e). <see cref="Pressed"/> is raised on that thread.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_APP_WORK = 0x8000 + 1;
    private const uint MOD_NOREPEAT = 0x4000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly object _gate = new();
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Dictionary<string, int> _idsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _namesById = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private readonly string _className = "GlobalAutocorrect.Hotkeys." + Guid.NewGuid().ToString("N");
    private readonly Native.WndProc _wndProc; // kept in a field so the delegate is not collected.

    private IntPtr _hwnd;
    private int _nextId = 1;
    private bool _disposed;
    private Exception? _startupError;

    public HotkeyManager()
    {
        _wndProc = WindowProc;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "HotkeyManager" };
        _thread.Start();
        _ready.Wait();
        if (_startupError is not null)
            throw new InvalidOperationException("Could not create the hotkey window.", _startupError);
    }

    /// <summary>Raised on the hotkey thread with the registered name when its shortcut is pressed.</summary>
    public event Action<string>? Pressed;

    /// <summary>
    /// Registers (or replaces) the named hotkey. Returns false if another app owns the combination
    /// or the manager is disposed.
    /// </summary>
    public bool Register(string name, Hotkey key)
    {
        ArgumentNullException.ThrowIfNull(name);
        return RunOnThread(() => RegisterCore(name, key), false);
    }

    public void Unregister(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        RunOnThread(() => { UnregisterCore(name); return true; }, true);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        if (_hwnd != IntPtr.Zero && _thread.IsAlive)
        {
            _work.Enqueue(() =>
            {
                foreach (var name in _idsByName.Keys.ToArray()) UnregisterCore(name);
                Native.DestroyWindow(_hwnd);
            });
            Native.PostMessage(_hwnd, WM_APP_WORK, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        _ready.Dispose();
    }

    private T RunOnThread<T>(Func<T> func, T fallback)
    {
        lock (_gate)
        {
            if (_disposed || !_thread.IsAlive) return fallback;
        }

        var result = fallback;
        using var done = new ManualResetEventSlim(false);
        _work.Enqueue(() =>
        {
            try { result = func(); }
            finally { done.Set(); }
        });

        if (!Native.PostMessage(_hwnd, WM_APP_WORK, IntPtr.Zero, IntPtr.Zero)) return fallback;
        // The thread may exit (disposal) before running the item; poll liveness so we never hang.
        while (!done.Wait(100))
        {
            if (!_thread.IsAlive) return fallback;
        }
        return result;
    }

    // Runs on the hotkey thread only.
    private bool RegisterCore(string name, Hotkey key)
    {
        UnregisterCore(name);
        var id = _nextId++;
        if (!Native.RegisterHotKey(_hwnd, id, (uint)key.Modifiers | MOD_NOREPEAT, key.Vk))
            return false;
        _idsByName[name] = id;
        _namesById[id] = name;
        return true;
    }

    private void UnregisterCore(string name)
    {
        if (!_idsByName.Remove(name, out var id)) return;
        _namesById.Remove(id);
        Native.UnregisterHotKey(_hwnd, id);
    }

    private void ThreadMain()
    {
        var hInstance = Native.GetModuleHandle(null);
        try
        {
            var wc = new Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = _className,
            };
            if (Native.RegisterClassEx(ref wc) == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            _hwnd = Native.CreateWindowEx(0, _className, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
            return;
        }

        _ready.Set();

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        Native.UnregisterClass(_className, hInstance);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_APP_WORK:
                while (_work.TryDequeue(out var item))
                {
                    try { item(); } catch { /* never let a work item kill the loop */ }
                }
                return IntPtr.Zero;

            case WM_HOTKEY:
                if (_namesById.TryGetValue((int)wParam, out var name))
                {
                    try { Pressed?.Invoke(name); } catch { /* handler errors must not stop the loop */ }
                }
                return IntPtr.Zero;

            case WM_DESTROY:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static class Native
    {
        public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

#pragma warning disable SYSLIB1054 // DllImport is fine for these few calls.
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);
#pragma warning restore SYSLIB1054
    }
}
