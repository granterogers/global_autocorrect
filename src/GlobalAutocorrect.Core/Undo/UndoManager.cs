using GlobalAutocorrect.Core.Targets;
using GlobalAutocorrect.Core.Text;
using GlobalAutocorrect.Core.Write;

namespace GlobalAutocorrect.Core.Undo;

/// <summary>The one most recent correction (D10). Kept in memory only, never shown or saved (14.5).</summary>
public sealed record CorrectionRecord(string TargetId, long Generation, int Start, string Original, string Replacement, string ContextBefore, long KeySeqAtWrite)
{
    /// <summary>D84: the whole words before the fix, where they start in the typed text, and the whole words after it.</summary>
    public string WordsBefore { get; init; } = Original;
    public string WordsAfter { get; init; } = Replacement;
    public int WordsStart { get; init; } = Start;
}

public enum UndoResult { Reverted, NothingToUndo, OtherField, TextChanged, Failed }

public sealed class UndoManager
{
    private const int ContextChars = 24;

    public CorrectionRecord? Last { get; private set; }

    public void Record(CorrectionNotice notice, PendingWrite w, string typedAfter, long generation)
    {
        string ctx = typedAfter[Math.Max(0, w.Start - ContextChars)..w.Start];
        // D84: the whole words around the fix, as the write guard and the mistake memory see them.
        static bool Joins(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’';
        int end = Math.Min(typedAfter.Length, w.Start + w.Replacement.Length), s = w.Start, e = end;
        while (s > 0 && Joins(typedAfter[s - 1])) s--;
        while (e < typedAfter.Length && Joins(typedAfter[e])) e++;
        Last = new CorrectionRecord(notice.TargetId, generation, w.Start, w.Original, w.Replacement, ctx, notice.KeySeqAtWrite)
        {
            WordsAfter = typedAfter[s..e],
            WordsBefore = typedAfter[s..w.Start] + w.Original + typedAfter[end..e],
            WordsStart = s,
        };
        Recent.Add(Last);
        if (Recent.Count > 8) Recent.RemoveAt(0);
    }

    /// <summary>D84: the last few fixes of this session, to notice one the user types back over (kept in memory only).</summary>
    public List<CorrectionRecord> Recent { get; } = [];

    public void Clear() { Last = null; Recent.Clear(); }

    /// <summary>
    /// For the global shortcut when the typing session has moved on (D9): finds the corrected text by its context in the
    /// current paragraph before the caret. Returns text-element counts for the write plus where the replacement starts in
    /// the normalised text, or null if it isn't there exactly once.
    /// </summary>
    public static (int CaretDistance, int DeleteCount, int ReplacementStart)? Locate(CorrectionRecord rec, string fieldBefore)
    {
        string before = TextNorm.Normalise(fieldBefore);
        int para = before.LastIndexOf('\n') + 1;
        string needle = rec.ContextBefore + rec.Replacement;
        int idx = before.IndexOf(needle, para, StringComparison.Ordinal);
        if (idx < 0 || before.IndexOf(needle, idx + 1, StringComparison.Ordinal) >= 0) return null;
        int end = idx + needle.Length;
        return (TextNorm.Elements(before[end..]), TextNorm.Elements(rec.Replacement), idx + rec.ContextBefore.Length);
    }

    /// <summary>4.8.c: the words the user reverted to, which go into the personal dictionary.</summary>
    public static IEnumerable<string> WordsToLearn(CorrectionRecord rec) =>
        Tokeniser.Tokenise(rec.Original).Where(t => t.IsWord && !t.IsProtected).Select(t => t.Text);
}
