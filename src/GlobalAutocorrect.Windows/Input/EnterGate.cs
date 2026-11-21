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
    public static int MaxHoldMs => Core.Learning.Tunables.EnterMaxHoldMs.Int;   // D155

    private enum State { Idle, Holding, Draining }

    private static readonly Lock Gate = new();
    private static readonly List<(uint Vk, uint Scan, uint Flags, bool Down)> Held = [];
    private static State _state;
    private static nint _window;
    private static long _heldAt;
    private static int _started;
    private static volatile bool _armed;
    // D166 (stress: keys after a released Enter came out 2–10 s late, in batches of 5, 18 and 22): the gate's timing ran
    // on the shared thread pool, which suggestion requests waiting on the model had taken up; a 60 ms step waited seconds
    // for a thread. Its own clock thread now: one deadline, woken when it changes.
    private static long _dueAt = long.MaxValue;
    private static readonly AutoResetEvent Wake = new(false);
    private static Thread? _clock;

    private static void Schedule(int ms)
    {
        Volatile.Write(ref _dueAt, Environment.TickCount64 + Math.Max(1, ms));
        if (_clock is null)
        {
            _clock = new Thread(ClockLoop) { IsBackground = true, Name = "EnterGate clock", Priority = ThreadPriority.AboveNormal };
            _clock.Start();
        }
        Wake.Set();
    }

    private static void Cancel() => Volatile.Write(ref _dueAt, long.MaxValue);

    private static void ClockLoop()
    {
        while (true)
        {
            long due = Volatile.Read(ref _dueAt);
            long now = Environment.TickCount64;
            if (due != long.MaxValue && now >= due)
            {
                Interlocked.CompareExchange(ref _dueAt, long.MaxValue, due);
                try { Timeout(); } catch (Exception) { /* never take the clock down */ }
                continue;
            }
            Wake.WaitOne(due == long.MaxValue ? -1 : (int)Math.Min(int.MaxValue, due - now));
        }
    }

    /// <summary>Set by the app each tick: the focused field sends on Enter and the app is running.</summary>
    public static bool Armed { get => _armed; set => _armed = value; }

    private static volatile int _holdMs = 1500;

    /// <summary>D156: how long the next hold may last, set with <see cref="Armed"/> (a chat box's wait, or an instant fix's).</summary>
    public static int HoldMs { get => _holdMs; set => _holdMs = value; }

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
            Schedule(_holdMs);
            return true;
        }
    }

    /// <summary>The engine's side: true once for each hold that began since it last asked.</summary>
    public static bool TakeStarted() => Interlocked.Exchange(ref _started, 0) == 1;

    /// <summary>
    /// D157: the fix Enter waits for is still being written (a slow field retries its placement): the hold goes on, up
    /// to <see cref="HardCapMs"/> from the Enter. Called each engine tick, so if the engine stops, the timer still lets go.
    /// </summary>
    public static void KeepHolding()
    {
        lock (Gate)
        {
            if (_state == State.Holding && Environment.TickCount64 - _heldAt < HardCapMs)
                Schedule(_holdMs);
        }
    }

    /// <summary>Let the held keys go, Enter first, in the order they came.</summary>
    public static void Release(string why)
    {
        lock (Gate)
        {
            if (_state != State.Holding) return;
            Cancel();
            SendHeldLocked(why);
        }
    }

    /// <summary>
    /// D156 (stress: "Dear Mr" came out "D EMArr": a replayed key was never seen coming back, the gate waited for it, and
    /// the keys held meanwhile were let go seconds late, among later ones). After a release, keys still wait behind it for
    /// <see cref="DrainMs"/>, then go too, and so on until none came: ordered by time, never by counting what came back.
    /// </summary>
    public const int DrainMs = 40;

    /// <summary>Hook thread: one of the replayed keys has come round (counted by the hook; nothing waits for it).</summary>
    public static void Replayed() { }

    /// <summary>
    /// D154 (stress, chat: the timer let Enter and 27 keys go while a fix was being placed, and they typed over the
    /// selected word): never in the middle of a write. The engine's own release comes between writes; the timer waits for
    /// the write's key hold to end, up to <see cref="HardCapMs"/>.
    /// </summary>
    public static int HardCapMs => Core.Learning.Tunables.EnterHardCapMs.Int;   // D155

    public static Func<bool> WriteInProgress { get; set; } = () => KeyHold.Active;

    private static void Timeout()
    {
        lock (Gate)
        {
            if (_state == State.Holding && WriteInProgress() && Environment.TickCount64 - _heldAt < HardCapMs)
            {
                Schedule(30);
                return;
            }
            if (_state == State.Holding) { SendHeldLocked("timeout"); return; }
            // Draining: keys that came after the release go now, behind it; none came: done.
            if (_state == State.Draining) { if (Held.Count > 0) SendHeldLocked("after"); else _state = State.Idle; }
        }
    }

    private static void SendHeldLocked(string why)
    {
        // D161 (stress, chat: "thanks for letting" came out "ltehtatniksn forg"; sandbox: "Hi Joanna," as "HiJ oanna,"):
        // let go together, Enter and the keys typed after it arrived within microseconds, and the page was still sending
        // the message, or making the new paragraph, when they came. Enter goes on its own; the rest follow a moment later.
        int upTo = Held.FindIndex(k => k.Vk == VK_RETURN && !k.Down);
        int take = upTo >= 0 && upTo < Held.Count - 1 ? upTo + 1 : Held.Count;
        var keys = Held.Take(take).ToArray();
        Held.RemoveRange(0, take);
        int next = Held.Count > 0 ? Math.Max(DrainMs, Core.Learning.Tunables.EnterGapMs.Int) : DrainMs;
        if (Foreground() != _window)
        {
            Held.Clear();
            _state = State.Idle;
            Diagnose?.Invoke($"enter-hold: {keys.Length} held key events dropped, the window changed (D154, no text)");
            return;
        }
        if (keys.Length == 0) { _state = State.Idle; return; }
        _state = State.Draining;
        Schedule(next);
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
        lock (Gate) { _state = State.Idle; Held.Clear(); _armed = false; _started = 0; _holdMs = 1500; Cancel(); }
    }
}
