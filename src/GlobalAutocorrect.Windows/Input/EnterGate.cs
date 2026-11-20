using GlobalAutocorrect.Windows.Native;
using GlobalAutocorrect.Windows.Write;
using static GlobalAutocorrect.Windows.Native.NativeMethods;

namespace GlobalAutocorrect.Windows.Input;

/// <summary>
/// D154 (user: "do it"; chat: AI fixes arrived after Enter had sent the message, so they were lost). In a field that
/// sends on Enter (learnt, <see cref="Core.Health.AppHealth.EnterSends"/>), Enter is held while a fix for the line is on
/// its way, and let go as soon as the fixes are in, or after <see cref="MaxHoldMs"/> whatever happens, by a timer of its
/// own. Keys typed during the hold wait behind it, in order. Held keys are never sent to another window: if the window
/// changed meanwhile, they are dropped (logged) rather than typed somewhere the user didn't mean.
/// </summary>
public static class EnterGate
{
    /// <summary>Replayed keys carry this tag; the hook passes them on as the user's own (counted, translated).</summary>
    public const nuint ReplayTag = 0x47414345;   // "GACE"

    /// <summary>The longest Enter is ever held.</summary>
    public const int MaxHoldMs = 1500;

    private enum State { Idle, Holding, Replaying }

    private static readonly Lock Gate = new();
    private static readonly List<(uint Vk, uint Scan, uint Flags, bool Down)> Held = [];
    private static State _state;
    private static int _replayLeft;
    private static nint _window;
    private static long _heldAt;
    private static int _started;
    private static volatile bool _armed;
    private static Timer? _timer;

    /// <summary>Set by the app each tick: the focused field sends on Enter and the app is running.</summary>
    public static bool Armed { get => _armed; set => _armed = value; }

    public static bool Holding { get { lock (Gate) return _state != State.Idle; } }

    /// <summary>Seams for tests.</summary>
    public static Func<INPUT[], bool> Send { get; set; } = SendInputWriter.Send;
    public static Func<nint> Foreground { get; set; } = GetForegroundWindow;
    public static Action<string>? Diagnose { get; set; }

    /// <summary>
    /// Hook thread, for the user's own key events only. True: the event is held (the hook swallows it). Enter starts a
    /// hold only when armed and with no modifier down (Shift+Enter is a line break, never a send).
    /// </summary>
    public static bool TryHold(uint vk, uint scan, uint flags, bool down, bool modifierDown)
    {
        lock (Gate)
        {
            if (_state != State.Idle) { Held.Add((vk, scan, flags, down)); return true; }
            if (!down || vk != VK_RETURN || modifierDown || !_armed) return false;
            _state = State.Holding;
            _window = Foreground();
            _heldAt = Environment.TickCount64;
            Held.Add((vk, scan, flags, down));
            Interlocked.Exchange(ref _started, 1);
            (_timer ??= new Timer(_ => Timeout())).Change(MaxHoldMs, System.Threading.Timeout.Infinite);
            return true;
        }
    }

    /// <summary>The engine's side: true once for each hold that began since it last asked.</summary>
    public static bool TakeStarted() => Interlocked.Exchange(ref _started, 0) == 1;

    /// <summary>Let the held keys go, Enter first, in the order they came.</summary>
    public static void Release(string why)
    {
        lock (Gate)
        {
            if (_state != State.Holding) return;
            _timer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            SendHeldLocked(why);
        }
    }

    /// <summary>Hook thread: one of the replayed keys has come round. New keys stay held until the last one has.</summary>
    public static void Replayed()
    {
        lock (Gate)
        {
            if (_state != State.Replaying || --_replayLeft > 0) return;
            if (Held.Count == 0) { _state = State.Idle; return; }
            // Keys typed while the replay was on its way: after it, in order (sent from a pool thread, not the hook).
            ThreadPool.QueueUserWorkItem(_ => { lock (Gate) if (_state == State.Replaying && _replayLeft == 0) SendHeldLocked("after"); });
        }
    }

    /// <summary>
    /// D154 (stress, chat: the timer let Enter and 27 keys go while a fix was being placed, and they typed over the
    /// selected word): never in the middle of a write. The engine's own release comes between writes; the timer waits for
    /// the write's key hold to end, up to <see cref="HardCapMs"/>.
    /// </summary>
    public const int HardCapMs = 4000;

    public static Func<bool> WriteInProgress { get; set; } = () => KeyHold.Active;

    private static void Timeout()
    {
        lock (Gate)
        {
            if (_state == State.Holding && WriteInProgress() && Environment.TickCount64 - _heldAt < HardCapMs)
            {
                _timer?.Change(30, System.Threading.Timeout.Infinite);
                return;
            }
            if (_state == State.Holding) { SendHeldLocked("timeout"); return; }
            // The replay never came round (the hook was removed): stop holding new keys, send what is left.
            if (_state == State.Replaying) { _replayLeft = 0; if (Held.Count > 0) SendHeldLocked("stuck"); else _state = State.Idle; }
        }
    }

    private static void SendHeldLocked(string why)
    {
        var keys = Held.ToArray();
        Held.Clear();
        if (Foreground() != _window)
        {
            _state = State.Idle;
            Diagnose?.Invoke($"enter-hold: {keys.Length} held key events dropped, the window changed (D154, no text)");
            return;
        }
        if (keys.Length == 0) { _state = State.Idle; return; }
        _state = State.Replaying;
        _replayLeft = keys.Length;
        (_timer ??= new Timer(_ => Timeout())).Change(1000, System.Threading.Timeout.Infinite);
        if (!Send(keys.Select(k => KeyHold.ToInput(k.Vk, k.Scan, k.Flags, k.Down, ReplayTag)).ToArray()))
        {
            _state = State.Idle;
            Diagnose?.Invoke($"enter-hold: replay failed (D154, no text)");
            return;
        }
        Diagnose?.Invoke($"enter-hold: released {keys.Length} key events ({why}) (D154, no text)");
    }

    /// <summary>Tests only.</summary>
    public static void Reset()
    {
        lock (Gate) { _state = State.Idle; Held.Clear(); _replayLeft = 0; _armed = false; _started = 0; _timer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); }
    }
}
