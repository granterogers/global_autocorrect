using System.Runtime.InteropServices;
using static GlobalAutocorrect.Windows.Native.NativeMethods;

namespace GlobalAutocorrect.Windows.Uia;

/// <summary>
/// Watches focus changes (WinEvents, out of context). Each change breaks the typing session and warms up the new field's
/// UIA connection, because Chromium and Electron only expose full text once an accessibility client connects (10.6.d).
/// Caret moves made by the app are not tracked here: keys and clicks already break the session.
/// </summary>
public sealed class FocusTracker : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_FOCUS = 0x8005;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;

    private delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint hmod, WinEventProc proc, uint pid, uint tid, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);

    private readonly WinEventProc _proc;   // kept alive while hooked
    private readonly UiaWorker _worker;
    private readonly Thread _thread;
    private uint _threadId;
    private int _warming;

    /// <summary>Raised on the tracker's thread with the newly focused window. Keep handlers short.</summary>
    public event Action<nint>? FocusChanged;

    public FocusTracker(UiaWorker worker)
    {
        _worker = worker;
        _proc = OnEvent;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var h1 = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            var h2 = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, 0, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            ready.Set();
            while (GetMessageW(out _, 0, 0, 0) > 0) { }
            UnhookWinEvent(h1);
            UnhookWinEvent(h2);
        }) { IsBackground = true, Name = "GA focus" };
        _thread.Start();
        ready.Wait();
    }

    private void OnEvent(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        FocusChanged?.Invoke(hwnd);
        // Warm up off this thread, at most one at a time; a slow app must not stall focus tracking.
        if (Interlocked.Exchange(ref _warming, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { _worker.Invoke(r => { r.Warm(); return true; }); }
                finally { Volatile.Write(ref _warming, 0); }
            });
        }
    }

    public void Dispose()
    {
        PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        _thread.Join(1000);
    }
}
