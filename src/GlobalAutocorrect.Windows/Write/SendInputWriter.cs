using System.Runtime.InteropServices;
using GlobalAutocorrect.Windows.Input;
using GlobalAutocorrect.Windows.Native;
using static GlobalAutocorrect.Windows.Native.NativeMethods;

namespace GlobalAutocorrect.Windows.Write;

/// <summary>
/// Writes edits as one SendInput batch relative to the caret: move left, delete, type, move back (10.7.a).
/// Windows never interleaves a single SendInput batch with user input, so the edit is atomic. Never uses
/// ValuePattern.SetValue (10.7.c).
/// </summary>
public sealed class SendInputWriter(nuint tag)
{
    /// <summary>The production writer: every event carries <see cref="InjectionTag.Value"/> (10.7.b).</summary>
    public static SendInputWriter Tagged { get; } = new(InjectionTag.Value);

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    /// <summary>Modifiers a user might still be holding when we write (Ctrl+Z, Ctrl+Alt+Z, Shift). Never Win: a lone Win
    /// release opens the Start menu, and AI writes already wait while it is held.</summary>
    private static readonly ushort[] Releasable = [VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU];

    /// <summary>
    /// D114: type every character as a Unicode packet, never a key press with Shift. A packet's character doesn't depend
    /// on Shift, so a Shift the user presses while the editor is still applying the batch can't turn it into capitals.
    /// </summary>
    public bool TypeAsUnicode { get; init; }

    /// <summary>Physical key state; replaceable in tests.</summary>
    public Func<int, bool> IsKeyDown { get; init; } = vk => IsDown(vk);

    /// <param name="caretDistance">Text elements between the end of the replaced range and the caret.</param>
    /// <param name="deleteCount">Text elements to remove (the original range).</param>
    public INPUT[] BuildEditBatch(int caretDistance, int deleteCount, string replacement)
    {
        var list = new List<INPUT>(caretDistance * 4 + deleteCount * 2 + replacement.Length * 2 + 12);
        // Undo runs while the user still holds Ctrl (or Ctrl+Alt): release held modifiers for the batch, then press them
        // again, or Left/Backspace/letters would arrive as Ctrl+Left, Ctrl+Backspace, AltGr+letter (found by the audit).
        var held = Releasable.Where(vk => IsKeyDown(vk)).ToArray();
        foreach (var vk in held) list.Add(Modifier(vk, up: true));
        for (int i = 0; i < caretDistance; i++) AddVk(list, VK_LEFT);
        for (int i = 0; i < deleteCount; i++) AddVk(list, VK_BACK);
        AddUnicode(list, replacement);
        for (int i = 0; i < caretDistance; i++) AddVk(list, VK_RIGHT);
        foreach (var vk in held.Reverse()) list.Add(Modifier(vk, up: false));
        return [.. list];
    }

    private INPUT Modifier(ushort vk, bool up)
    {
        uint flags = (vk is VK_RCONTROL or VK_RMENU ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
        return Key(vk, (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC), flags);
    }

    public INPUT[] BuildTyping(string text)
    {
        var list = new List<INPUT>(text.Length * 2);
        AddUnicode(list, text);
        return [.. list];
    }

    /// <summary>Sends the whole batch in one call. False if Windows accepted fewer events (e.g. UIPI blocked it).</summary>
    public static bool Send(INPUT[] inputs) =>
        inputs.Length == 0 || SendInput((uint)inputs.Length, inputs, InputSize) == inputs.Length;

    public bool ApplyEdit(int caretDistance, int deleteCount, string replacement) =>
        Send(BuildEditBatch(caretDistance, deleteCount, replacement));

    public bool Type(string text) => Send(BuildTyping(text));

    /// <summary>D99: Right × <paramref name="count"/>, one batch, with held modifiers released around it as in an edit.</summary>
    public bool MoveRight(int count)
    {
        var list = new List<INPUT>(count * 2 + 12);
        var held = Releasable.Where(vk => IsKeyDown(vk)).ToArray();
        foreach (var vk in held) list.Add(Modifier(vk, up: true));
        for (int i = 0; i < count; i++) AddVk(list, VK_RIGHT);
        foreach (var vk in held.Reverse()) list.Add(Modifier(vk, up: false));
        return Send([.. list]);
    }

    private void AddVk(List<INPUT> list, ushort vk)
    {
        ushort scan = (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
        // Arrow keys live on the extended cluster; without the flag some apps see numpad keys.
        uint ext = vk is VK_LEFT or VK_RIGHT ? KEYEVENTF_EXTENDEDKEY : 0;
        list.Add(Key(vk, scan, ext));
        list.Add(Key(vk, scan, ext | KEYEVENTF_KEYUP));
    }

    /// <summary>
    /// Types text as real key presses on the current layout (D22). Some apps (Win11 Notepad) handle VK_PACKET (Unicode)
    /// events late and out of order within a batch, so Unicode packets are only used for characters with no key.
    /// </summary>
    private void AddUnicode(List<INPUT> list, string text)
    {
        nint hkl = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
        bool caps = CapsLockOn();
        foreach (char c in text)
        {
            if (c == '\n')
            {
                // D109: a line break in retyped text is Shift+Enter, a new line without sending in chat editors (Enter
                // sends in Teams). VkKeyScan gives Ctrl+Enter for "\n", which some editors take as "send".
                ushort shiftScan = (ushort)MapVirtualKeyW(VK_SHIFT, MAPVK_VK_TO_VSC), enterScan = (ushort)MapVirtualKeyW(VK_RETURN, MAPVK_VK_TO_VSC);
                list.Add(Key(VK_SHIFT, shiftScan, 0));
                list.Add(Key(VK_RETURN, enterScan, 0));
                list.Add(Key(VK_RETURN, enterScan, KEYEVENTF_KEYUP));
                list.Add(Key(VK_SHIFT, shiftScan, KEYEVENTF_KEYUP));
                continue;
            }
            if (TypeAsUnicode)
            {
                list.Add(Key(0, c, KEYEVENTF_UNICODE));
                list.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
                continue;
            }
            short scan = VkKeyScanExW(c, hkl);
            byte vk = (byte)(scan & 0xFF), mods = (byte)((scan >> 8) & 0xFF);
            if (scan == -1 || (mods & 0x06) != 0)   // no key, or needs Ctrl/Alt (AltGr): fall back to a Unicode packet
            {
                list.Add(Key(0, c, KEYEVENTF_UNICODE));
                list.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
                continue;
            }
            bool shift = (mods & 0x01) != 0;
            if (caps && char.IsLetter(c)) shift = !shift;   // Caps Lock flips letters only
            ushort sc = (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
            if (shift) list.Add(Key(VK_SHIFT, (ushort)MapVirtualKeyW(VK_SHIFT, MAPVK_VK_TO_VSC), 0));
            list.Add(Key(vk, sc, 0));
            list.Add(Key(vk, sc, KEYEVENTF_KEYUP));
            if (shift) list.Add(Key(VK_SHIFT, (ushort)MapVirtualKeyW(VK_SHIFT, MAPVK_VK_TO_VSC), KEYEVENTF_KEYUP));
        }
    }

    /// <summary>
    /// D108, user report (Intercom: "why the capitals?", "WE RECOMMEND YOU TO MIGRATE FROM OEM TO"): Caps Lock as the
    /// app being typed into sees it. GetKeyState answers for the calling thread's input state, which only moves on as
    /// that thread reads keyboard messages. The engine thread never does, so it could report Caps Lock on long after the
    /// user turned it off (after typing "OEM" and "BSB"), and a retyped tail of 39 letters went in as capitals. Attached
    /// to the foreground thread's input for the call, it reads that thread's current state.
    /// </summary>
    public static bool CapsLockOn()
    {
        uint target = GetWindowThreadProcessId(GetForegroundWindow(), out _), me = GetCurrentThreadId();
        bool attached = target != 0 && target != me && AttachThreadInput(me, target, true);
        try { return (GetKeyState(VK_CAPITAL) & 1) != 0; }
        finally { if (attached) AttachThreadInput(me, target, false); }
    }

    private INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = tag } },
    };
}
