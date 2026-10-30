namespace GlobalAutocorrect.Core.Instant;

/// <summary>
/// How likely a single slip on a UK keyboard turns <c>intended</c> into <c>typed</c> (D23). Used with word frequency as a
/// noisy-channel score: doubled keys, adjacent swaps and neighbouring keys are common; random edits are not.
/// </summary>
public static class TypoChannel
{
    private static readonly string[] Rows = ["qwertyuiop", "asdfghjkl", "zxcvbnm"];
    private static readonly Dictionary<char, HashSet<char>> Near = BuildAdjacency();

    public const double DoubledKey = 0.05, Swap = 0.04, MissedDouble = 0.04, NeighbourKey = 0.03,
                        ExtraNeighbour = 0.02, Other = 0.01, OtherSubstitution = 0.003;

    public static bool AreNeighbours(char a, char b) =>
        Near.TryGetValue(char.ToLowerInvariant(a), out var s) && s.Contains(char.ToLowerInvariant(b));

    /// <summary>Probability-like weight for one edit (distance 1) from <paramref name="intended"/> to <paramref name="typed"/>.</summary>
    public static double Likelihood(string typed, string intended)
    {
        string t = typed.ToLowerInvariant(), c = intended.ToLowerInvariant();
        if (t.Length == c.Length + 1)   // an extra key in what was typed
        {
            for (int i = 0; i < t.Length; i++)
            {
                if (string.CompareOrdinal(t.Remove(i, 1), c) != 0) continue;
                bool doubled = (i > 0 && t[i] == t[i - 1]) || (i + 1 < t.Length && t[i] == t[i + 1]);
                if (doubled) return DoubledKey;
                bool fat = (i > 0 && AreNeighbours(t[i], t[i - 1])) || (i + 1 < t.Length && AreNeighbours(t[i], t[i + 1]));
                return fat ? ExtraNeighbour : Other;
            }
        }
        else if (t.Length + 1 == c.Length)   // a key was missed
        {
            for (int i = 0; i < c.Length; i++)
            {
                if (string.CompareOrdinal(c.Remove(i, 1), t) != 0) continue;
                bool wasDouble = (i > 0 && c[i] == c[i - 1]) || (i + 1 < c.Length && c[i] == c[i + 1]);
                return wasDouble ? MissedDouble : Other;
            }
        }
        else if (t.Length == c.Length)
        {
            int first = -1, count = 0;
            for (int i = 0; i < t.Length; i++)
                if (t[i] != c[i]) { if (first < 0) first = i; count++; }
            if (count == 1) return AreNeighbours(t[first], c[first]) ? NeighbourKey : OtherSubstitution;
            if (count == 2 && first + 1 < t.Length && t[first] == c[first + 1] && t[first + 1] == c[first]) return Swap;
        }
        return OtherSubstitution;
    }

    /// <summary>
    /// D30: the most likely way two slips turn <paramref name="intended"/> into <paramref name="typed"/>, through one
    /// intermediate spelling ("but" → "bu" → "by": a missed key, then a neighbouring key). Zero unless they are exactly
    /// two edits apart.
    /// </summary>
    public static double TwoSlipLikelihood(string typed, string intended)
    {
        string t = typed.ToLowerInvariant(), c = intended.ToLowerInvariant();
        if (Math.Abs(t.Length - c.Length) > 2) return 0;
        double best = 0;
        foreach (var mid in OneEditAway(c))
        {
            if (Text.Similarity.DamerauLevenshtein(t, mid) != 1) continue;
            best = Math.Max(best, Likelihood(mid, c) * Likelihood(t, mid));
        }
        return best;
    }

    /// <summary>Two slips where at most one is a random one: a missed key with a neighbouring key, say.</summary>
    public const double TwoLikelySlips = Other * NeighbourKey;

    private static IEnumerable<string> OneEditAway(string s)
    {
        const string Letters = "abcdefghijklmnopqrstuvwxyz";
        for (int i = 0; i < s.Length; i++) yield return s.Remove(i, 1);
        for (int i = 0; i + 1 < s.Length; i++) yield return string.Concat(s.AsSpan(0, i), [s[i + 1], s[i]], s.AsSpan(i + 2));
        foreach (char l in Letters)
        {
            for (int i = 0; i <= s.Length; i++) yield return s.Insert(i, l.ToString());
            for (int i = 0; i < s.Length; i++) if (s[i] != l) yield return string.Concat(s.AsSpan(0, i), [l], s.AsSpan(i + 1));
        }
    }

    private static Dictionary<char, HashSet<char>> BuildAdjacency()
    {
        var d = new Dictionary<char, HashSet<char>>();
        for (int r = 0; r < Rows.Length; r++)
        {
            for (int i = 0; i < Rows[r].Length; i++)
            {
                var set = new HashSet<char>();
                void Add(int row, int col)
                {
                    if (row >= 0 && row < Rows.Length && col >= 0 && col < Rows[row].Length) set.Add(Rows[row][col]);
                }
                Add(r, i - 1); Add(r, i + 1);
                Add(r - 1, i); Add(r - 1, i + 1);   // staggered rows: the row above sits half a key to the right
                Add(r + 1, i - 1); Add(r + 1, i);
                d[Rows[r][i]] = set;
            }
        }
        return d;
    }
}
