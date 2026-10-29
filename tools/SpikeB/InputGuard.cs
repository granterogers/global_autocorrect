using GlobalAutocorrect.Windows.Native;
using static GlobalAutocorrect.Windows.Native.NativeMethods;

namespace SpikeB;

/// <summary>2.8: notices real (non-injected) keyboard or mouse input so a test that is typing can stop.</summary>
internal sealed class InputGuard : IDisposable
{
    private readonly Thread _thread;
    private uint _threadId;
    private nint _kb, _mouse;
    private LowLevelHookProc? _kbProc, _mouseProc;   // kept alive while hooked
    private volatile bool _physical;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _counts = new();

    public bool PhysicalInputSeen => _physical;

    /// <summary>Event-type counts for diagnosing false positives. Never records which key.</summary>
    public IReadOnlyDictionary<string, int> Counts => _counts;

    private void Count(string kind) => _counts.AddOrUpdate(kind, 1, (_, v) => v + 1);
    public void Reset() => _physical = false;

    public InputGuard()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _kbProc = (n, w, l) =>
            {
                if (n >= 0)
                {
                    bool injected = (System.Runtime.InteropServices.Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l).flags & LLKHF_INJECTED) != 0;
                    Count(injected ? "key-injected" : "key-physical");
                    if (!injected) _physical = true;
                }
                return CallNextHookEx(_kb, n, w, l);
            };
            _mouseProc = (n, w, l) =>
            {
                // 0x0200 = WM_MOUSEMOVE: ignore tiny moves (a desk knock); clicks and wheel count.
                if (n >= 0)
                {
                    bool injected = (System.Runtime.InteropServices.Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l).flags & LLMHF_INJECTED) != 0;
                    Count($"mouse-0x{w:X4}-{(injected ? "injected" : "physical")}");
                    if (w != 0x0200 && !injected) _physical = true;
                }
                return CallNextHookEx(_mouse, n, w, l);
            };
            nint mod = GetModuleHandleW(null);
            _kb = SetWindowsHookExW(WH_KEYBOARD_LL, _kbProc, mod, 0);
            _mouse = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, mod, 0);
            ready.Set();
            while (GetMessageW(out _, 0, 0, 0) > 0) { }
            UnhookWindowsHookEx(_kb);
            UnhookWindowsHookEx(_mouse);
        }) { IsBackground = true, Name = "InputGuard" };
        _thread.Start();
        ready.Wait();
    }

    public void Dispose()
    {
        PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        _thread.Join(1000);
    }
}
