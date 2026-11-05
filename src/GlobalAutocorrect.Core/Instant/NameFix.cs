using GlobalAutocorrect.Core.Text;

namespace GlobalAutocorrect.Core.Instant;

/// <summary>
/// D92, user request (Intercom shows "Reply Christopher Moyer"; the reply began "Hi Chrisopher,"): a misspelt name is
/// fixed to the name the app shows near the field. Names are held in memory only (8.6).
/// </summary>
public static class NameFix
{
    /// <summary>At most this many names are kept for a field.</summary>
    public const int MaxNames = 40;

    /// <summary>Capitalised words of three letters or more from the labels near the field: the names it may show.</summary>
    public static IReadOnlyList<string> Candidates(IEnumerable<string> labels)
    {
        var names = new List<string>();
        foreach (var label in labels)
            foreach (var t in Tokeniser.Tokenise(label))
            {
                if (!t.IsWord || t.IsProtected || t.Length < 3 || !char.IsUpper(t.Text[0]) || !t.Text.All(c => char.IsLetter(c) || c is '\'' or '-')) continue;
                if (!names.Contains(t.Text, StringComparer.Ordinal)) names.Add(t.Text);
                if (names.Count == MaxNames) return names;
            }
        return names;
    }

    /// <summary>
    /// The name shown nearby that <paramref name="word"/> is a slip of, or null. Close in letters (one slip for a short
    /// name, two from six letters), the same first letter, and the only name that close. A word that is already a word or
    /// a name is left alone.
    /// </summary>
    public static string? For(string word, IReadOnlyList<string> names, Func<string, bool> isValid)
    {
        if (names.Count == 0 || word.Length < 4 || !word.All(char.IsLetter)) return null;
        if (names.Any(n => string.Equals(n, word, StringComparison.OrdinalIgnoreCase))) return null;
        if (isValid(word) || isValid(word.ToLowerInvariant())) return null;
        string w = word.ToLowerInvariant();
        string? best = null;
        int bestDistance = int.MaxValue;
        bool tie = false;
        foreach (var name in names)
        {
            string n = name.ToLowerInvariant();
            if (n[0] != w[0] || n.Length < 4) continue;
            int d = Similarity.DamerauLevenshtein(w, n);
            bool shortName = Math.Min(w.Length, n.Length) < 6;
            if (d > (shortName ? 1 : 2)) continue;
            // A short name only for a likely keyboard slip: "Moyre" is Moyer, "Mayer" is someone else.
            if (shortName && TypoChannel.Likelihood(w, n) < TypoChannel.ExtraNeighbour) continue;
            if (d < bestDistance) { best = name; bestDistance = d; tie = false; }
            else if (d == bestDistance && !string.Equals(best, name, StringComparison.OrdinalIgnoreCase)) tie = true;
        }
        if (best is null || tie) return null;
        return word.Length > 1 && word.All(char.IsUpper) ? best.ToUpperInvariant() : best;
    }
}
