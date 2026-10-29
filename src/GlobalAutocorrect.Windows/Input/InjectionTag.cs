namespace GlobalAutocorrect.Windows.Input;

/// <summary>Marks every event we inject, so our own hook ignores it (10.7.b).</summary>
public static class InjectionTag
{
    /// <summary>"GAC1" as a pointer-sized value in KEYBDINPUT.dwExtraInfo.</summary>
    public const nuint Value = 0x47414331;
}
