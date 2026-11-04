namespace GlobalAutocorrect.Windows.Uia;

/// <summary>
/// D83: when to ask the field for its font name and size (7.6). They are read from the character just before the caret,
/// so a read of an empty field (or one where the caret follows a line break) learns nothing. D79 read them once per
/// field and kept the answer, empty or not; Intercom's reply box is first read empty, when focus lands there (D67's early
/// reload), so the suggestion was sized from the caret, a whole spaced-out line tall, and came out too big (user report,
/// twice). Now an empty answer is asked again on later reads, once there is a letter or digit before the caret, a few
/// times at most per field, so an app that never reports a size costs only those few calls.
/// </summary>
public sealed class FieldStyleCache
{
    public const int MaxTries = 4;

    private string _field = "";
    private int _tries;

    public string? Font { get; private set; }
    public double? SizePt { get; private set; }

    /// <summary>True when the font should be read now, for field <paramref name="fieldId"/> with <paramref name="before"/> before the caret.</summary>
    public bool ShouldRead(string fieldId, string before)
    {
        if (fieldId != _field) { _field = fieldId; _tries = 0; Font = null; SizePt = null; }
        if (Font is not null && SizePt is not null) return false;
        if (_tries >= MaxTries || before.Length == 0 || !char.IsLetterOrDigit(before[^1])) return false;
        return true;
    }

    /// <summary>What the field said; a null keeps any earlier answer.</summary>
    public void Store(string? font, double? sizePt)
    {
        _tries++;
        Font ??= font;
        SizePt ??= sizePt;
    }
}
