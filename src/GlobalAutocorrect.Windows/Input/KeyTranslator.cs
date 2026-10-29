using GlobalAutocorrect.Core.Session;
using static GlobalAutocorrect.Windows.Native.NativeMethods;

namespace GlobalAutocorrect.Windows.Input;

/// <summary>
/// Turns raw key events into what they mean for the typed text (4.2), using the foreground window's keyboard layout
/// (UK, 1.9). Runs on the engine thread, never in the hook. Tracks modifier state from the event stream itself.
/// </summary>
public sealed class KeyTranslator
{
    private const uint ToUnicodeNoStateChange = 0x4;   // don't disturb dead-key state (Windows 10 1607+)
    private readonly byte[] _state = new byte[256];
    private readonly char[] _buf = new char[8];
    private bool _lShift, _rShift, _lCtrl, _rCtrl, _lAlt, _rAlt, _lWin, _rWin, _caps;

    public KeyTranslator() => _caps = (GetKeyState(VK_CAPITAL) & 1) != 0;

    private bool Shift => _lShift || _rShift;
    private bool Ctrl => _lCtrl || _rCtrl;
    private bool Alt => _lAlt || _rAlt;
    private bool Win => _lWin || _rWin;

    /// <summary>Key-downs give one input per character produced (usually one); key-ups only update modifiers.</summary>
    public IEnumerable<KeyInput> Translate(RawInput r)
    {
        uint vk = r.Vk;
        if (UpdateModifier(vk, r.IsDown) || !r.IsDown)
        {
            if (r.IsDown) yield return new KeyInput(KeyKind.Other, '\0', r.Seq, r.AtMs);
            yield break;
        }
        if (r.Injected) { yield return new KeyInput(KeyKind.Foreign, '\0', r.Seq, r.AtMs); yield break; }   // 4.2
        if (vk == VK_CAPITAL) { _caps = !_caps; yield return new KeyInput(KeyKind.Other, '\0', r.Seq, r.AtMs); yield break; }

        KeyKind? kind = Classify(vk);
        if (kind is { } k) { yield return new KeyInput(k, k == KeyKind.Char ? '\n' : '\0', r.Seq, r.AtMs); yield break; }

        int n = ToChars(vk, r.Scan);
        if (n <= 0) { yield return new KeyInput(Ctrl || Alt || Win ? KeyKind.Shortcut : KeyKind.Other, '\0', r.Seq, r.AtMs); yield break; }
        for (int i = 0; i < n; i++)
        {
            char c = _buf[i];
            if (char.IsControl(c)) { yield return new KeyInput(KeyKind.Shortcut, '\0', r.Seq, r.AtMs); yield break; }
            yield return KeyInput.Of(c, r.Seq, r.AtMs);
        }
    }

    /// <summary>Keys whose meaning doesn't depend on the layout. Null = translate to characters.</summary>
    private KeyKind? Classify(uint vk)
    {
        if ((Ctrl && !Alt && vk == 'V') || (Shift && vk == VK_INSERT)) return KeyKind.Paste;   // 4.2, 14.4
        if (Win) return KeyKind.Shortcut;
        if ((Ctrl || Alt) && !(Ctrl && Alt)) return KeyKind.Shortcut;   // Ctrl+Alt together is AltGr: may produce é, €
        return vk switch
        {
            VK_BACK => KeyKind.Backspace,
            VK_DELETE => KeyKind.Delete,
            VK_RETURN => KeyKind.Char,   // as '\n'
            VK_TAB or VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN or VK_HOME or VK_END or VK_PRIOR or VK_NEXT => KeyKind.Navigation,
            VK_ESCAPE or VK_INSERT or VK_NUMLOCK or VK_SCROLL or VK_PAUSE => KeyKind.Other,
            >= VK_F1 and <= VK_F24 => KeyKind.Other,
            _ => null,
        };
    }

    private int ToChars(uint vk, uint scan)
    {
        Array.Clear(_state);
        if (Shift) _state[VK_SHIFT] = 0x80;
        if (Ctrl) _state[VK_CONTROL] = 0x80;
        if (Alt) _state[VK_MENU] = 0x80;
        if (_caps) _state[VK_CAPITAL] = 0x01;
        nint hkl = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
        return ToUnicodeEx(vk, scan, _state, _buf, _buf.Length, ToUnicodeNoStateChange, hkl);
    }

    private bool UpdateModifier(uint vk, bool down)
    {
        switch (vk)
        {
            case VK_LSHIFT: _lShift = down; return true;
            case VK_RSHIFT: _rShift = down; return true;
            case VK_SHIFT: _lShift = down; return true;
            case VK_LCONTROL: _lCtrl = down; return true;
            case VK_RCONTROL: _rCtrl = down; return true;
            case VK_CONTROL: _lCtrl = down; return true;
            case VK_LMENU: _lAlt = down; return true;
            case VK_RMENU: _rAlt = down; return true;
            case VK_MENU: _lAlt = down; return true;
            case VK_LWIN: _lWin = down; return true;
            case VK_RWIN: _rWin = down; return true;
            default: return false;
        }
    }
}
