namespace GlobalAutocorrect.Core.Text;

public enum EditKind { Spacing, Punctuation, Apostrophe, Capitalisation, Substitution, Insertion, Deletion }

/// <summary>
/// One small replacement in ORIGINAL coordinates (4.4.e). [Start, End) is the minimal changed range and
/// Replacement its new text. [SpanStart, SpanEnd) is the whole-token range, used for protection, the
/// 4.6.a "word being typed" check and the underline (4.7).
/// </summary>
public sealed record TextEdit(
    int Start,
    int End,
    string Replacement,
    EditKind Kind,
    IReadOnlyList<string> OriginalWords,
    IReadOnlyList<string> NewWords,
    int SpanStart,
    int SpanEnd)
{
    public string OriginalText(string original) => original[Start..End];

    public string ApplyTo(string text) => string.Concat(text.AsSpan(0, Start), Replacement, text.AsSpan(End));

    /// <summary>Applies non-overlapping edits right to left so earlier offsets stay valid.</summary>
    public static string ApplyAll(string text, IEnumerable<TextEdit> edits)
    {
        int limit = int.MaxValue;
        foreach (var e in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
        {
            if (e.End > limit) throw new ArgumentException("edits overlap", nameof(edits));
            text = e.ApplyTo(text);
            limit = e.Start;
        }
        return text;
    }
}
