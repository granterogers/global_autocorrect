using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.Windows.Hotkeys;

namespace GlobalAutocorrect.App.ViewModels;

/// <summary>
/// One shortcut with a "recording" box (9.3.d): the user clicks it, presses a combination, and it is checked with
/// <see cref="Hotkey.TryParse"/> before it is saved.
/// </summary>
public sealed partial class ShortcutEditor : ObservableObject
{
    private readonly Func<string> _get;
    private readonly Action<string> _save;
    private readonly Func<string> _other;
    private readonly string _otherName;
    private readonly string _default;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayText))] private string _text;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayText))] private bool _isRecording;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";

    /// <param name="other">Current text of the other shortcut, so the two can't be the same.</param>
    public ShortcutEditor(string title, string description, string defaultValue, Func<string> get, Action<string> save, Func<string> other, string otherName)
    {
        Title = title;
        Description = description;
        _default = defaultValue;
        _get = get;
        _save = save;
        _other = other;
        _otherName = otherName;
        _text = get();
    }

    public string Title { get; }
    public string Description { get; }
    public bool HasError => Error.Length > 0;
    public string DisplayText => IsRecording ? "Press a shortcut…" : Text;

    public void StartRecording()
    {
        Error = "";
        IsRecording = true;
    }

    public void StopRecording() => IsRecording = false;

    public void Refresh()
    {
        if (!IsRecording) Text = _get();
    }

    /// <summary>
    /// Handles one key press while recording. Returns true when the press was used (so the box swallows it).
    /// A bare modifier keeps waiting; Esc cancels; anything else is checked and saved, or rejected with a friendly message.
    /// </summary>
    public bool Record(ModifierKeys modifiers, Key key)
    {
        if (!IsRecording) return false;
        if (IsModifierKey(key)) return true;
        if (key == Key.Escape && modifiers == ModifierKeys.None) { StopRecording(); return true; }

        string? keyName = KeyName(key);
        if (keyName is null)
        {
            Error = "That key can't be used. Try a letter, a number or a function key such as F9.";
            return true;
        }

        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(keyName);
        TrySet(string.Join('+', parts));
        return true;
    }

    /// <summary>Validates and saves a shortcut written as text, such as "Ctrl+Alt+P". Returns false and shows why when it is rejected.</summary>
    public bool TrySet(string text)
    {
        if (!Hotkey.TryParse(text, out var hotkey))
        {
            Error = "That combination won't work. Start with Ctrl, Alt or the Windows key, then add a letter, number or function key, for example Ctrl+Alt+P.";
            return false;
        }

        string canonical = hotkey.ToString();
        if (string.Equals(canonical, _other(), StringComparison.OrdinalIgnoreCase))
        {
            Error = $"That's already used for {_otherName}. Please choose a different one.";
            return false;
        }

        Error = "";
        Text = canonical;
        IsRecording = false;
        _save(canonical);
        return true;
    }

    [RelayCommand]
    private void Reset()
    {
        IsRecording = false;
        TrySet(_default);
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None or Key.DeadCharProcessed or Key.ImeProcessed;

    /// <summary>The key names Hotkey.TryParse understands; null for anything else.</summary>
    internal static string? KeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.F1 and <= Key.F24) return "F" + (key - Key.F1 + 1);
        return key switch
        {
            Key.Space => "Space",
            Key.Pause => "Pause",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            _ => null,
        };
    }
}

/// <summary>Shortcuts (9.3.d): the pause and undo shortcuts.</summary>
public sealed class ShortcutsViewModel
{
    public ShortcutsViewModel(ISettingsService settings)
    {
        ShortcutEditor? pause = null, undo = null;
        pause = new ShortcutEditor("Pause or resume", "Switch Global Autocorrect off and on from anywhere.",
            Hotkey.DefaultPause.ToString(),
            () => settings.Settings.PauseShortcut,
            v => settings.Update(settings.Settings with { PauseShortcut = v }),
            () => undo!.Text, "the undo shortcut");
        undo = new ShortcutEditor("Undo the last correction", "Puts back what you typed, from anywhere, if the text hasn't changed since.",
            Hotkey.DefaultUndo.ToString(),
            () => settings.Settings.UndoShortcut,
            v => settings.Update(settings.Settings with { UndoShortcut = v }),
            () => pause.Text, "the pause shortcut");
        Pause = pause;
        Undo = undo;
    }

    public ShortcutEditor Pause { get; }
    public ShortcutEditor Undo { get; }

    public void Refresh()
    {
        Pause.Refresh();
        Undo.Refresh();
    }
}
