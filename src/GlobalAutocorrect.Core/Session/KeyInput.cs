namespace GlobalAutocorrect.Core.Session;

public enum KeyKind
{
    /// <summary>A printable character or Enter ('\n') from a physical key.</summary>
    Char,
    Backspace,
    /// <summary>Forward delete: changes text after the caret only.</summary>
    Delete,
    /// <summary>Arrows, Home/End, PgUp/PgDn, Tab moving focus: the caret moved.</summary>
    Navigation,
    /// <summary>Ctrl/Alt/Win combinations other than those handled specially.</summary>
    Shortcut,
    /// <summary>Ctrl+V, Shift+Insert (4.2, 14.4).</summary>
    Paste,
    /// <summary>Input injected by another tool (dictation, expanders): LLKHF_INJECTED without our tag (4.2).</summary>
    Foreign,
    Click,
    FocusChange,
}

public readonly record struct KeyInput(KeyKind Kind, char Char, long Seq, long AtMs)
{
    public static KeyInput Of(char c, long seq, long at) => new(KeyKind.Char, c, seq, at);
}

public enum SessionBreakReason { None, Navigation, Shortcut, Paste, Foreign, Click, FocusChange, BackspaceIntoExisting, FieldMismatch, Overflow, Desync }
