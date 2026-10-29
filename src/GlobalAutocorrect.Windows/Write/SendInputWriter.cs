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

    /// <param name="caretDistance">Text elements between the end of the replaced range and the caret.</param>
    /// <param name="deleteCount">Text elements to remove (the original range).</param>
    public INPUT[] BuildEditBatch(int caretDistance, int deleteCount, string replacement)
    {
        var list = new List<INPUT>(caretDistance * 4 + deleteCount * 2 + replacement.Length * 2);
        for (int i = 0; i < caretDistance; i++) AddVk(list, VK_LEFT);
        for (int i = 0; i < deleteCount; i++) AddVk(list, VK_BACK);
        AddUnicode(list, replacement);
        for (int i = 0; i < caretDistance; i++) AddVk(list, VK_RIGHT);
        return [.. list];
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
        bool caps = (GetKeyState(VK_CAPITAL) & 1) != 0;
        foreach (char c in text)
        {
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

    private INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = tag } },
    };
}
