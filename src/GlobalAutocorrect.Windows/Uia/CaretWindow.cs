namespace GlobalAutocorrect.Windows.Uia;

/// <summary>
/// D88, user report "WhatsApp: nothing fixed" (and the Claude app's mismatches with 13 objects in the field): the text
/// read either side of the caret must be the field's own. Some Chromium editors (WhatsApp's message box) let a range
/// moved 400 characters back run on out of the box into the chat above it, and their endpoint comparison is wrong, so
/// every write looked like a mismatch and was refused. The reader also reads from the field's start to the caret and
/// from the caret to the field's end (moving an endpoint to the field's own range does work), and keeps those where
/// they are the shorter, consistent answer.
/// </summary>
public static class CaretWindow
{
    /// <param name="before">The usual window: up to <paramref name="beforeChars"/> characters back from the caret.</param>
    /// <param name="after">Up to the usual number of characters on from the caret.</param>
    /// <param name="fieldBefore">From the field's start to the caret, read up to <paramref name="beforeChars"/> + 1 characters
    /// (so a field with more text than the window comes back longer than it); null if it couldn't be read.</param>
    /// <param name="fieldAfter">From the caret to the field's end, up to the usual number; null if it couldn't be read.</param>
    public static (string Before, string After) InField(string before, string after, string? fieldBefore, string? fieldAfter, int beforeChars)
    {
        // The field starts nearer than the window reaches: only the field's part of it. It must be the window's end, or
        // the two reads disagree and the usual window stays (a document longer than the window, say, reads its start).
        if (fieldBefore is not null && fieldBefore.Length <= beforeChars && fieldBefore.Length < before.Length
            && before.EndsWith(fieldBefore, StringComparison.Ordinal))
            before = fieldBefore;
        if (fieldAfter is not null && fieldAfter.Length < after.Length && after.StartsWith(fieldAfter, StringComparison.Ordinal))
            after = fieldAfter;
        return (before, after);
    }
}
