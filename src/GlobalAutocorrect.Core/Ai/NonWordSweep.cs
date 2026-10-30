using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Text;

namespace GlobalAutocorrect.Core.Ai;

/// <summary>One word the sweep replaces, in the coordinates of the text it was given.</summary>
public sealed record SweepFix(int Start, int End, string Original, string Replacement);

/// <summary>
/// D38: after the AI's reply has been through the rules, a word that still isn't a word ("hve", "amaybe") gets the
/// dictionary word that best fits both the slip and the words around it ("would've hve been" → "have", not "he"). The
/// user's rule: an obvious misspelling should never be left in place just because the model rearranged the sentence
/// around it. Only lower-case words the dictionary and the personal dictionary don't know; the typed word competes, so slang that fits stays; names,
/// numbers and code-like tokens are never touched, and an unclear winner is left alone.
/// </summary>
public sealed class NonWordSweep(ISpellChecker speller, ISuggester suggester, Func<string, bool> isPersonalWord, Func<string, string, long> pairCount)
{
    /// <summary>The best candidate must score this many times the next one.</summary>
    public double WinRatio { get; init; } = 3;

    /// <param name="accepted">The model's edits the rules let through: those words are already fixed.</param>
    /// <param name="rejected">The model's edits the rules refused. Only a word one of these touches is swept: the model
    /// agrees it's wrong, its fix just wasn't safe. A word the model left alone ("helpdesk") is never changed here.</param>
    public IReadOnlyList<SweepFix> Find(string text, IReadOnlyList<TextEdit> accepted, IReadOnlyList<TextEdit> rejected)
    {
        var tokens = Tokeniser.Tokenise(text);
        var words = tokens.Where(t => t.IsWord).ToArray();
        var fixes = new List<SweepFix>();
        var used = new HashSet<int>();
        for (int i = 0; i < words.Length; i++)
        {
            var t = words[i];
            if (used.Contains(i)) continue;
            // Whole-word spans: a spacing fix ("remem ber" → "remember") covers only the space as its minimal range, and
            // the sweep then "fixed" the "ber" half into "rememher" (found on the tuning set).
            if (t.IsProtected || t.End == text.Length || accepted.Any(e => e.SpanStart < t.End && e.SpanEnd > t.Start)) continue;   // never the word still being typed
            if (!rejected.Any(e => e.SpanStart < t.End && e.SpanEnd > t.Start)) continue;
            string w = t.Text;
            if (!IsNonWord(w)) continue;
            // D50: a word split into pieces ("ne arly", "the ir"): if joining this piece with its neighbour(s) makes a
            // dictionary word, join them. The commonest such word wins.
            if (Join(text, words, i, accepted) is { } join)
            {
                fixes.Add(join.Fix);
                foreach (int k in join.Covers) used.Add(k);
                continue;
            }
            // Neighbours count only if only spaces separate them, and only if they are words themselves.
            string? prev = i > 0 && IsGap(text, words[i - 1].End, t.Start) && Known(words[i - 1].Text) ? words[i - 1].Text : null;
            string? next = i + 1 < words.Length && IsGap(text, t.End, words[i + 1].Start) && Known(words[i + 1].Text) ? words[i + 1].Text : null;
            if (Best(w.ToLowerInvariant(), prev, next) is { } pick)
                fixes.Add(new SweepFix(t.Start, t.End, w, InstantCorrector.MatchCase(w, pick)));
        }
        return fixes;
    }

    private (SweepFix Fix, int[] Covers)? Join(string text, Token[] words, int i, IReadOnlyList<TextEdit> accepted)
    {
        bool Free(int k) => k >= 0 && k < words.Length && !words[k].IsProtected && words[k].End < text.Length
                            && !accepted.Any(e => e.SpanStart < words[k].End && e.SpanEnd > words[k].Start)
                            && words[k].Text.All(char.IsLetter) && !char.IsUpper(words[k].Text[0]);
        bool OneSpace(int a, int b) => text.AsSpan(words[a].End, words[b].Start - words[a].End) is " ";
        (SweepFix, int[])? best = null; long bestFreq = 0;
        foreach (var (from, to) in new[] { (i - 1, i), (i, i + 1), (i - 1, i + 1), (i, i + 2) })
        {
            if (!Free(from) || !Free(to)) continue;
            bool joined = true;
            for (int k = from; k < to; k++) joined &= Free(k) && OneSpace(k, k + 1);
            if (!joined) continue;
            string word = string.Concat(Enumerable.Range(from, to - from + 1).Select(k => words[k].Text));
            if (word.Length < 3 || !speller.IsValid(word)) continue;
            long freq = suggester.Lookup(word.ToLowerInvariant(), 1) is [{ Distance: 0 } c] ? c.Frequency : 1;
            if (freq <= bestFreq) continue;
            bestFreq = freq;
            best = (new SweepFix(words[from].Start, words[to].End, text[words[from].Start..words[to].End], word),
                    Enumerable.Range(from, to - from + 1).ToArray());
        }
        return best;
    }

    private bool IsNonWord(string w) =>
        w.Length >= 2 && !char.IsUpper(w[0]) && !w.Any(char.IsDigit) && !w.Contains('\'') && !Known(w);

    private bool Known(string w) => speller.IsValid(w) || isPersonalWord(w);

    private static string Letters(string s) => string.Concat(s.Where(char.IsLetter)).ToLowerInvariant();

    private static bool IsGap(string text, int from, int to) => to > from && text.AsSpan(from, to - from).Trim(' ').IsEmpty;

    /// <summary>How likely it is that what was typed is what was meant (no slip), against the slip weights.</summary>
    private const double NoSlip = 0.9;

    private string? Best(string w, string? prev, string? next)
    {
        var candidates = suggester.Lookup(w, 12);
        double Fit(string c, long freq) => Math.Sqrt(freq + 1)
            * (1 + (prev is null ? 0 : pairCount(prev, c))) * (1 + (next is null ? 0 : pairCount(c, next)));
        // The word as typed competes too. Slang the UK word list knows ("gonna be") fits its neighbours and stays;
        // junk the list happens to contain ("the other dy and") doesn't, and a real word wins.
        // A string in neither the dictionary nor the UK word list can't be what was meant: it doesn't compete at all.
        long typedFreq = candidates.FirstOrDefault(c => c.Distance == 0 && c.Word == w).Frequency;
        double asTyped = typedFreq == 0 ? 0 : NoSlip * Fit(w, typedFreq);
        var scored = candidates
            // Same letters with a hyphen or space added ("helpdesk" → "help-desk") is style, not a typo fix.
            .Where(c => c.Distance is 1 or 2 && speller.IsValid(c.Word) && Letters(c.Word) != Letters(w))
            .Select(c => (c.Word, c.Frequency, Slip: Channel(w, c)))
            .Where(c => c.Slip >= TypoChannel.TwoLikelySlips)   // one slip, or two likely ones; never two random edits
            .Select(c => (c.Word, Score: c.Slip * Fit(c.Word, c.Frequency)))
            .OrderByDescending(x => x.Score).ToArray();
        if (scored.Length == 0) return null;
        double runnerUp = Math.Max(asTyped, scored.Length > 1 ? scored[1].Score : 0);
        return scored[0].Score >= WinRatio * runnerUp ? scored[0].Word : null;   // not clear: leave it
    }

    private static double Channel(string typed, SpellSuggestion c) => c.Distance == 1
        ? TypoChannel.Likelihood(typed, c.Word)
        : TypoChannel.TwoSlipLikelihood(typed, c.Word);
}
