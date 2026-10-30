namespace GlobalAutocorrect.Windows.Uia;

/// <summary>
/// D71: which focus events mean the user is now somewhere else. Chromium repeats the focus event for the element that
/// already has focus (when its accessibility tree is rebuilt, mid-typing): each one broke the typing session, so the
/// typed record restarted in the middle of a word ("Lo|oking", CKEditor in CI; D60 stopped the damage, D69 found the
/// cause). An event identical to the previous one (same window, object and child) carries no news and is ignored.
/// Caret moves inside a field are not focus changes: clicks and keys already break the session for those.
/// </summary>
public sealed class FocusFilter
{
    private (uint Event, nint Hwnd, int Object, int Child)? _last;

    public bool IsNew(uint ev, nint hwnd, int idObject, int idChild)
    {
        var key = (ev, hwnd, idObject, idChild);
        if (_last == key) return false;
        _last = key;
        return true;
    }
}
