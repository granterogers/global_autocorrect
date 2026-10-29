using GlobalAutocorrect.Core.Text;

namespace GlobalAutocorrect.Core.Eval;

/// <summary>Per-message eval counts (6.3.a-c).</summary>
public sealed record MessageScore(int NeedFix, int Fixed, int CorrectWords, int FalseEdits, bool Exact);

/// <summary>Summed counts over many messages, with the three headline rates (6.3.a-c, 6.3.e).</summary>
public sealed record Aggregate(int Messages, int NeedFix, int Fixed, int CorrectWords, int FalseEdits, int ExactMatches)
{
    /// <summary>6.3.a: share of noisy errors the output fixed. 1 when there was nothing to fix.</summary>
    public double ErrorsFixedRate => NeedFix == 0 ? 1 : (double)Fixed / NeedFix;

    /// <summary>6.3.b: share of already-correct words the output changed. 0 when there were none.</summary>
    public double FalseEditRate => CorrectWords == 0 ? 0 : (double)FalseEdits / CorrectWords;

    /// <summary>6.3.c: share of messages whose output equals the clean text (whitespace-normalised).</summary>
    public double ExactMatchRate => Messages == 0 ? 0 : (double)ExactMatches / Messages;
}

/// <summary>
/// Scores a model output against the clean and noisy versions of a message. Clean, noisy and output are
/// aligned word by word; a clean word is "correct" if the noisy text already had it, and "fixed" if the output has it.
/// </summary>
public static class Scorer
{
    /// <summary>
    /// All comparisons are case-sensitive and exact. Clean words that sit in a merge, split or deletion unit
    /// of an alignment never count as matched. Words the noisy text adds (ACount == 0 units, e.g. a doubled
    /// "the the") are not clean words: each counts toward NeedFix, and counts as fixed only when the output is
    /// an exact match with the same word count as the clean text (a deliberate approximation).
    /// </summary>
    public static MessageScore Score(string clean, string noisy, string output)
    {
        var c = Words(clean);
        var n = Words(noisy);
        var o = Words(output);

        var cn = SequenceAligner.Align(c, n, WordDiff.PairCost);
        var co = SequenceAligner.Align(c, o, WordDiff.PairCost);
        var matchN = MatchFlags(c, n, cn);
        var matchO = MatchFlags(c, o, co);

        bool exact = string.Join(" ", c) == string.Join(" ", o);

        int needFix = 0, fixedCount = 0, correct = 0, falseEdits = 0;
        for (int i = 0; i < c.Count; i++)
        {
            if (matchN[i])
            {
                correct++;
                if (!matchO[i]) falseEdits++;
            }
            else
            {
                needFix++;
                if (matchO[i]) fixedCount++;
            }
        }

        bool extrasRemoved = o.Count == c.Count && exact;
        foreach (var u in cn)
        {
            if (u.ACount != 0) continue;
            needFix += u.BCount;
            if (extrasRemoved) fixedCount += u.BCount;
        }

        return new MessageScore(needFix, fixedCount, correct, falseEdits, exact);
    }

    public static Aggregate Combine(IEnumerable<MessageScore> scores)
    {
        int messages = 0, needFix = 0, fixedCount = 0, correct = 0, falseEdits = 0, exact = 0;
        foreach (var s in scores)
        {
            messages++;
            needFix += s.NeedFix;
            fixedCount += s.Fixed;
            correct += s.CorrectWords;
            falseEdits += s.FalseEdits;
            if (s.Exact) exact++;
        }
        return new Aggregate(messages, needFix, fixedCount, correct, falseEdits, exact);
    }

    /// <summary>Splits on whitespace and drops empties; punctuation stays attached to its word.</summary>
    public static IReadOnlyList<string> Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool[] MatchFlags(IReadOnlyList<string> clean, IReadOnlyList<string> other, IReadOnlyList<AlignedUnit> units)
    {
        var flags = new bool[clean.Count];
        foreach (var u in units)
        {
            if (u.ACount == 1 && u.BCount == 1)
                flags[u.AStart] = clean[u.AStart] == other[u.BStart];
        }
        return flags;
    }
}
