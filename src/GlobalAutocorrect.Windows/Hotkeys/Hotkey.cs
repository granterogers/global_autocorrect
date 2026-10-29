using System.Globalization;

namespace GlobalAutocorrect.Windows.Hotkeys;

/// <summary>Modifier flags; values match the RegisterHotKey MOD_* constants.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A global shortcut: modifiers plus a Windows virtual-key code (10.3.e, 8.2, 4.8.b).</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint Vk)
{
    public static Hotkey DefaultPause => Parse("Ctrl+Alt+P");

    public static Hotkey DefaultUndo => Parse("Ctrl+Alt+Z");

    public static Hotkey Parse(string text)
    {
        if (!TryParse(text, out var hotkey))
            throw new FormatException("Not a valid global shortcut.");
        return hotkey;
    }

    /// <summary>
    /// Parses text such as "Ctrl+Alt+P" or "ctrl + alt + z". At least one of Ctrl/Alt/Win is required,
    /// otherwise the shortcut would steal normal typing.
    /// </summary>
    public static bool TryParse(string text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = HotkeyModifiers.None;
        uint? vk = null;

        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0) return false;

            var mod = ParseModifier(part);
            if (mod != HotkeyModifiers.None)
            {
                if ((mods & mod) != 0) return false; // duplicate modifier
                mods |= mod;
                continue;
            }

            if (vk is not null) return false; // more than one key
            var parsed = ParseKey(part);
            if (parsed is null) return false;
            vk = parsed;
        }

        if (vk is null) return false;
        if ((mods & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0) return false;

        hotkey = new Hotkey(mods, vk.Value);
        return true;
    }

    /// <summary>Canonical form, e.g. "Ctrl+Alt+P" (order Ctrl, Alt, Shift, Win, key).</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & HotkeyModifiers.Ctrl) != 0) parts.Add("Ctrl");
        if ((Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((Modifiers & HotkeyModifiers.Win) != 0) parts.Add("Win");
        parts.Add(KeyName(Vk));
        return string.Join('+', parts);
    }

    private static HotkeyModifiers ParseModifier(string part) => part.ToLowerInvariant() switch
    {
        "ctrl" or "control" => HotkeyModifiers.Ctrl,
        "alt" => HotkeyModifiers.Alt,
        "shift" => HotkeyModifiers.Shift,
        "win" or "windows" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    private static uint? ParseKey(string part)
    {
        if (part.Length == 1)
        {
            var c = char.ToUpperInvariant(part[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
            return null;
        }

        var lower = part.ToLowerInvariant();
        switch (lower)
        {
            case "space": return 0x20;
            case "pause": return 0x13;
            case "insert": return 0x2D;
            case "home": return 0x24;
            case "end": return 0x23;
        }

        if (lower[0] == 'f'
            && int.TryParse(lower.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n is >= 1 and <= 24)
        {
            return (uint)(0x70 + n - 1);
        }

        return null;
    }

    private static string KeyName(uint vk)
    {
        if (vk is >= 'A' and <= 'Z') return ((char)vk).ToString();
        if (vk is >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1).ToString(CultureInfo.InvariantCulture);
        return vk switch
        {
            0x20 => "Space",
            0x13 => "Pause",
            0x2D => "Insert",
            0x24 => "Home",
            0x23 => "End",
            _ => "0x" + vk.ToString("X2", CultureInfo.InvariantCulture),
        };
    }
}
