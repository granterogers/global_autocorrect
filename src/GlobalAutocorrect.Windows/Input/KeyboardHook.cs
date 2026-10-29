using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Windows.Native;
using static GlobalAutocorrect.Windows.Native.NativeMethods;

namespace GlobalAutocorrect.Windows.Input;

public enum RawKind { Key, MouseDown, AcceptSuggestion, UndoLocal }

/// <summary>One event from the hook: timestamp and sequence only, no translation yet (10.3.a).</summary>
public readonly record struct RawInput(RawKind Kind, uint Vk, uint Scan, uint Flags, bool IsDown, bool Injected, long Seq, long AtMs);

/// <summary>
/// Low-level keyboard and mouse hooks on a dedicated thread (10.3.a). The callback only timestamps, numbers and enqueues,
/// then returns, because Windows silently removes a slow low-level hook (12.6). Events we injected (tagged) are ignored
/// (10.7.b). Tab and Ctrl+Z are swallowed only when their arm says so (7.4, 4.8.a).
/// </summary>
public sealed class KeyboardHook : IKeyMonitor, IDisposable
{
    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B;
    private const uint WM_APP_REINSTALL = 0x8001;

    private readonly Channel<RawInput> _channel = Channel.CreateUnbounded<RawInput>(new() { SingleReader = true });
    private readonly bool _untaggedInjectedIsPhysical;
    private readonly LowLevelHookProc _kbProc, _mouseProc;   // fields keep the delegates alive while hooked
    private Thread? _thread;
    private uint _threadId;
    private nint _kb, _mouse;
    private long _seq;
    private long _lastKeyAtMs = -100_000;
    private int _lastCallbackTick = Environment.TickCount;
    private volatile bool _swallowTabUp, _swallowZUp;

    public InputArm SuggestionArm { get; } = new();
    public InputArm UndoArm { get; } = new();
    public ChannelReader<RawInput> Events => _channel.Reader;
    public int Reinstalls { get; private set; }

    public long LatestKeySeq => Interlocked.Read(ref _seq);
    public long LastKeyAtMs => Interlocked.Read(ref _lastKeyAtMs);
    public bool ModifierHeld => IsDown(VK_SHIFT) || IsDown(VK_CONTROL) || IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN);

    /// <param name="untaggedInjectedIsPhysical">E2E only (GA_E2E=1): the test typer's untagged SendInput counts as the user.</param>
    public KeyboardHook(bool untaggedInjectedIsPhysical = false)
    {
        _untaggedInjectedIsPhysical = untaggedInjectedIsPhysical;
        _kbProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            Install();
            ready.Set();
            while (GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                if (msg.message == WM_APP_REINSTALL) { Uninstall(); Install(); Reinstalls++; }
            }
            Uninstall();
        }) { IsBackground = true, Name = "GA keyboard hook", Priority = ThreadPriority.Highest };
        _thread.Start();
        ready.Wait();
    }

    /// <summary>
    /// 12.6: if Windows has dropped the hook, keyboard/mouse input keeps happening but our callback stops. Call every few
    /// seconds; re-installs when the last input is clearly newer than our last callback.
    /// </summary>
    public void CheckAlive()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return;
        int lastInput = unchecked((int)info.dwTime);
        if (unchecked(lastInput - Volatile.Read(ref _lastCallbackTick)) > 3000)
        {
            Volatile.Write(ref _lastCallbackTick, Environment.TickCount);
            PostThreadMessageW(_threadId, WM_APP_REINSTALL, 0, 0);
        }
    }

    private void Install()
    {
        nint mod = GetModuleHandleW(null);
        _kb = SetWindowsHookExW(WH_KEYBOARD_LL, _kbProc, mod, 0);
        _mouse = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, mod, 0);
    }

    private void Uninstall()
    {
        if (_kb != 0) UnhookWindowsHookEx(_kb);
        if (_mouse != 0) UnhookWindowsHookEx(_mouse);
        _kb = _mouse = 0;
    }

    private static long NowMs(long ts) => (long)Stopwatch.GetElapsedTime(0, ts).TotalMilliseconds;

    private nint KeyboardProc(int nCode, nint wParam, nint lParam)
    {
        Volatile.Write(ref _lastCallbackTick, Environment.TickCount);
        if (nCode < 0) return CallNextHookEx(_kb, nCode, wParam, lParam);
        KBDLLHOOKSTRUCT k;
        unsafe { k = *(KBDLLHOOKSTRUCT*)lParam; }   // direct read: no marshalling on the hot path
        if (k.dwExtraInfo == InjectionTag.Value) return CallNextHookEx(_kb, nCode, wParam, lParam);   // ours (10.7.b)

        bool injected = (k.flags & LLKHF_INJECTED) != 0 && !_untaggedInjectedIsPhysical;
        bool down = wParam is WM_KEYDOWN or WM_SYSKEYDOWN;
        long at = NowMs(Stopwatch.GetTimestamp());

        if (!down)
        {
            if (k.vkCode == VK_TAB && _swallowTabUp) { _swallowTabUp = false; return 1; }
            if (k.vkCode == 'Z' && _swallowZUp) { _swallowZUp = false; return 1; }
            // Key-ups don't change text and don't count in the sequence; the translator needs them for modifier state.
            _channel.Writer.TryWrite(new RawInput(RawKind.Key, k.vkCode, k.scanCode, k.flags, false, injected, Interlocked.Read(ref _seq), at));
            return CallNextHookEx(_kb, nCode, wParam, lParam);
        }

        long prev = Interlocked.Read(ref _seq);
        if (!injected)
        {
            bool ctrl = IsDown(VK_CONTROL), alt = IsDown(VK_MENU), shift = IsDown(VK_SHIFT), win = IsDown(VK_LWIN) || IsDown(VK_RWIN);
            // 7.4: Tab takes the suggestion only if it is showing and nothing happened since it was shown.
            if (k.vkCode == VK_TAB && !ctrl && !alt && !win && !shift && SuggestionArm.TryConsume(prev))
            {
                _swallowTabUp = true;
                Enqueue(RawKind.AcceptSuggestion, k, true, false, at);
                return 1;
            }
            // 4.8.a: Ctrl+Z straight after a correction, caret unchanged, reverts it.
            if (k.vkCode == 'Z' && ctrl && !alt && !shift && !win && UndoArm.TryConsume(prev))
            {
                _swallowZUp = true;
                Enqueue(RawKind.UndoLocal, k, true, false, at);
                return 1;
            }
        }
        SuggestionArm.Disarm();
        UndoArm.Disarm();
        Enqueue(RawKind.Key, k, true, injected, at);
        return CallNextHookEx(_kb, nCode, wParam, lParam);
    }

    private nint MouseProc(int nCode, nint wParam, nint lParam)
    {
        Volatile.Write(ref _lastCallbackTick, Environment.TickCount);
        if (nCode >= 0 && wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
        {
            MSLLHOOKSTRUCT m;
            unsafe { m = *(MSLLHOOKSTRUCT*)lParam; }
            if (m.dwExtraInfo != InjectionTag.Value)
            {
                // A click can move the caret: it is a session break and counts in the sequence (D6).
                SuggestionArm.Disarm();
                UndoArm.Disarm();
                long seq = Interlocked.Increment(ref _seq);
                long at = NowMs(Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _lastKeyAtMs, at);
                _channel.Writer.TryWrite(new RawInput(RawKind.MouseDown, 0, 0, m.flags, true, false, seq, at));
            }
        }
        return CallNextHookEx(_mouse, nCode, wParam, lParam);
    }

    private void Enqueue(RawKind kind, KBDLLHOOKSTRUCT k, bool down, bool injected, long at)
    {
        // Swallowed Tab/Ctrl+Z never reach the app, so they don't advance the sequence the write gate compares.
        long seq = kind == RawKind.Key ? Interlocked.Increment(ref _seq) : Interlocked.Read(ref _seq);
        Interlocked.Exchange(ref _lastKeyAtMs, at);
        _channel.Writer.TryWrite(new RawInput(kind, k.vkCode, k.scanCode, k.flags, down, injected, seq, at));
    }

    public void Dispose()
    {
        if (_thread is null) return;
        PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        _thread.Join(1000);
        _channel.Writer.TryComplete();
        _thread = null;
    }
}
