using GlobalAutocorrect.Core.Targets;
using GlobalAutocorrect.Core.Text;
using GlobalAutocorrect.Core.Write;

namespace GlobalAutocorrect.Core.Undo;

/// <summary>The one most recent correction (D10). Kept in memory only, never shown or saved (14.5).</summary>
public sealed record CorrectionRecord(string TargetId, long Generation, int Start, string Original, string Replacement, string ContextBefore, long KeySeqAtWrite);

public enum UndoResult { Reverted, NothingToUndo, OtherField, TextChanged, Failed }

public sealed class UndoManager
{
    private const int ContextChars = 24;

    public CorrectionRecord? Last { get; private set; }

    public void Record(CorrectionNotice notice, PendingWrite w, string typedAfter, long generation)
    {
        string ctx = typedAfter[Math.Max(0, w.Start - ContextChars)..w.Start];
        Last = new CorrectionRecord(notice.TargetId, generation, w.Start, w.Original, w.Replacement, ctx, notice.KeySeqAtWrite);
    }

    public void Clear() => Last = null;

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
