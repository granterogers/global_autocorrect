namespace GlobalAutocorrect.Windows.Input;

/// <summary>
/// D105: makes an edit batch land only where it was aimed. The write gate checks that no key has arrived since it read
/// the field, then sends the batch, but a key the user had already pressed could still be on its way to our hook (the
/// hook runs on its own thread). Windows passes that key to the app before the batch, so the batch's Left×n started one
/// character off ("oe has" fixed to "oerhas") or ran with Shift held, which turned the arrows into a selection that the
/// Backspace deleted and typed the fix in capitals ("os I'm working…" became "oSO", real-typing test in Windows Sandbox).
/// The hook sees every event in the order the app will receive it, ours included. Each fenced batch carries its own
/// number in <c>dwExtraInfo</c>; when the hook sees its first event it compares the key count with the one the gate
/// checked, and whether a modifier is down. If anything changed, it swallows every event of that batch, so the app gets
/// none of it and the gate tries again later. The user's own keys are never held back or delayed (4.6.e).
/// </summary>
public static class WriteFence
{
    /// <summary>High half of a fenced batch's tag ("GAC2"); the low half is the batch number.</summary>
    public const ulong Marker = 0x47414332UL << 32;

    private static readonly Lock Gate = new();
    private static uint _next;
    private static uint _armed;
    private static long _armedSeq = -1;
    private static uint _decided;
    private static bool _decidedBlocked;
    private static readonly Dictionary<uint, bool> Outcomes = [];   // batch → blocked (the last few)

    public static bool IsFenced(nuint tag) => ((ulong)tag & 0xFFFF_FFFF_0000_0000UL) == Marker;

    /// <summary>A new batch number, armed with the key count the gate checked. Its tag goes on every event of the batch.</summary>
    public static nuint Arm(long expectedKeySeq)
    {
        lock (Gate)
        {
            _next = _next == uint.MaxValue ? 1 : _next + 1;
            _armed = _next;
            _armedSeq = expectedKeySeq;
            Outcomes.Remove(_armed);
            return (nuint)(Marker | _armed);
        }
    }

    /// <summary>
    /// The hook's verdict for an event of a fenced batch: true to swallow it. Decided once, at the batch's first event,
    /// from the key count then and whether the user holds a modifier; every later event of the batch gets the same.
    /// </summary>
    public static bool Swallow(nuint tag, long keySeqNow, bool modifierDown, string? modifiers = null)
    {
        uint batch = (uint)((ulong)tag & 0xFFFF_FFFF);
        lock (Gate)
        {
            if (batch != _decided)
            {
                _decided = batch;
                // Not the batch the gate armed last (a stale one): never let it through.
                _decidedBlocked = batch != _armed || keySeqNow != _armedSeq || modifierDown;
                Outcomes[batch] = _decidedBlocked;
                if (Outcomes.Count > 32) Outcomes.Remove(Outcomes.Keys.Min());
                Trace?.Invoke($"fence: batch={batch} armed={_armed} keys={keySeqNow}/{_armedSeq} {modifiers} blocked={_decidedBlocked}");
            }
            return _decidedBlocked;
        }
    }

    /// <summary>D115: each verdict, for the screen tests' log only (GA_E2E=1): numbers and key states, never text.</summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>What happened to a batch: null until the hook has seen it, then true if it was swallowed.</summary>
    public static bool? Blocked(nuint tag)
    {
        uint batch = (uint)((ulong)tag & 0xFFFF_FFFF);
        lock (Gate) return Outcomes.TryGetValue(batch, out bool b) ? b : null;
    }

    /// <summary>Tests only: forget every batch.</summary>
    public static void Reset()
    {
        lock (Gate) { _next = _armed = _decided = 0; _armedSeq = -1; _decidedBlocked = false; Outcomes.Clear(); }
    }
}
