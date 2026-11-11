using GlobalAutocorrect.Windows.Native;
using static GlobalAutocorrect.Windows.Native.NativeMethods;

namespace GlobalAutocorrect.Windows.Input;

/// <summary>
/// D122: while a fix on an earlier line is written by moving the caret there (UI Automation), the user's keys are held
/// by the hook and replayed, in order, the moment the caret is back. A key that reached the field while the caret was on
/// the fix would land in the middle of the earlier line ("Best Wishes,w"). The hold lasts as long as the write (tens of
/// milliseconds, after the user's 1.5 s pause, D117) and never more than <see cref="MaxHoldMs"/>: a timer lets go even if
/// the write never finishes, so the keyboard can't be left dead. Keys are still counted and tracked as typed when they
/// arrive; only their delivery to the app waits.
/// </summary>
public static class KeyHold
{
    /// <summary>"GAC3": replayed keys. The hook passes them straight on (they were counted when they were held).</summary>
    public const nuint ReplayTag = 0x47414333;

    public const int MaxHoldMs = 3000;

    /// <summary>Sends the replayed keys; tests replace it so nothing is typed.</summary>
    public static Func<INPUT[], bool> Sender { get; set; } = Write.SendInputWriter.Send;

    private static readonly Lock Gate = new();
    private static readonly List<INPUT> Held = [];
    private static bool _on;
    private static int _generation;
    private static Timer? _safety;

    public static bool Active { get { lock (Gate) return _on; } }

    /// <summary>Starts holding the user's keys. From the hook's next event on, nothing the user types reaches the app.</summary>
    public static void Begin()
    {
        lock (Gate)
        {
            _on = true;
            int gen = ++_generation;
            _safety?.Dispose();
            _safety = new Timer(_ => { lock (Gate) if (_generation != gen) return; Release(); }, null, MaxHoldMs, Timeout.Infinite);
        }
    }

    /// <summary>The hook's call for one of the user's events: true if it is held (the hook swallows it).</summary>
    public static bool TryHold(uint vk, uint scan, uint llFlags, bool down)
    {
        lock (Gate)
        {
            if (!_on) return false;
            Held.Add(ToInput(vk, scan, llFlags, down));
            return true;
        }
    }

    /// <summary>Replays every held key, in order, and stops holding. Keys arriving while it replays are held and replayed after them.</summary>
    public static int Release()
    {
        int replayed = 0;
        while (true)
        {
            INPUT[] batch;
            lock (Gate)
            {
                if (!_on) return replayed;
                if (Held.Count == 0)
                {
                    _on = false;
                    _generation++;
                    _safety?.Dispose();
                    _safety = null;
                    return replayed;
                }
                batch = [.. Held];
                Held.Clear();
            }
            Sender(batch);
            replayed += batch.Length;
        }
    }

    private static INPUT ToInput(uint vk, uint scan, uint llFlags, bool down)
    {
        const uint VK_PACKET = 0xE7;
        uint flags = down ? 0 : KEYEVENTF_KEYUP;
        if ((llFlags & LLKHF_EXTENDED) != 0) flags |= KEYEVENTF_EXTENDEDKEY;
        ushort wVk = (ushort)vk;
        if (vk == VK_PACKET) { flags |= KEYEVENTF_UNICODE; wVk = 0; }   // a typed character with no key (IME, emoji panel)
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = wVk, wScan = (ushort)scan, dwFlags = flags, dwExtraInfo = ReplayTag } },
        };
    }

    /// <summary>Tests only.</summary>
    public static int HeldCount { get { lock (Gate) return Held.Count; } }
}
