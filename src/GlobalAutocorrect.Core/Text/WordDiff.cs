namespace GlobalAutocorrect.Core.Text;

/// <summary>
/// Word-level diff between what the user typed and the model's cleaned text (4.4.e).
/// Unchanged tokens are fixed first with an exact LCS; each changed stretch between them is then aligned
/// with merge/split moves and split into small, independent edits, so rules can reject one and keep the rest (4.5.c).
/// </summary>
public static class WordDiff
{
    public static IReadOnlyList<TextEdit> Compute(string original, string cleaned)
    {
        var a = Tokeniser.Tokenise(original).Where(t => t.Kind != TokenKind.Space).ToArray();
        var b = Tokeniser.Tokenise(cleaned).Where(t => t.Kind != TokenKind.Space).ToArray();
        var edits = new List<TextEdit>();

        int pa = -1, pb = -1;
        foreach (var (ia, ib) in Lcs(a, b).Append((a.Length, b.Length)))
        {
            ProcessHunk(original, cleaned, a, b, pa + 1, ia, pb + 1, ib, edits);
            pa = ia;
            pb = ib;
        }
        return edits;
    }

    /// <summary>Cost of pairing two tokens; shared by the diff and the eval scorer.</summary>
    public static double PairCost(string x, string y)
    {
        bool px = IsPunct(x), py = IsPunct(y);
        if (px && py) return x == y ? 0 : 0.3;
        if (px != py) return 1.5;
        if (x == y) return 0;
        string kx = Similarity.Key(x), ky = Similarity.Key(y);
        return kx == ky ? 0.02 : Similarity.NormDist(kx, ky);
    }

    private static bool IsPunct(string s) => s.Length > 0 && !s.Any(char.IsLetterOrDigit);

    private static void ProcessHunk(string original, string cleaned, Token[] a, Token[] b,
        int aFrom, int aTo, int bFrom, int bTo, List<TextEdit> edits)
    {
        int oCur = aFrom > 0 ? a[aFrom - 1].End : 0;
        int cCur = bFrom > 0 ? b[bFrom - 1].End : 0;
        int oLimit = aTo < a.Length ? a[aTo].Start : original.Length;
        int cLimit = bTo < b.Length ? b[bTo].Start : cleaned.Length;

        if (aTo > aFrom || bTo > bFrom)
        {
            var aText = a[aFrom..aTo].Select(t => t.Text).ToArray();
            var bText = b[bFrom..bTo].Select(t => t.Text).ToArray();
            // Same letters, only the spaces moved ("ev eninginstead" → "evening instead"): one spacing edit for the stretch.
            if (aText.Length > 0 && bText.Length > 0 && aText.Length + bText.Length > 2
                && string.Concat(aText.Select(Similarity.Key)) == string.Concat(bText.Select(Similarity.Key)))
            {
                var at = a[aFrom..aTo];
                var bt = b[bFrom..bTo];
                AddEdit(original, cleaned, oCur, at[^1].End, cCur, bt[^1].End, at, bt, at[0].Start, at[^1].End, edits);
                oCur = at[^1].End;
                cCur = bt[^1].End;
                AddEdit(original, cleaned, oCur, oLimit, cCur, cLimit, [], [], oCur, oLimit, edits);
                return;
            }
            foreach (var u in SequenceAligner.Align(aText, bText, PairCost))
            {
                var at = a.AsSpan(aFrom + u.AStart, u.ACount).ToArray();
                var bt = b.AsSpan(bFrom + u.BStart, u.BCount).ToArray();
                int oEnd = u.ACount > 0 ? at[^1].End : oCur;
                int cEnd = u.BCount > 0 ? bt[^1].End : cCur;
                int spanStart = u.ACount > 0 ? at[0].Start : oCur;
                AddEdit(original, cleaned, oCur, oEnd, cCur, cEnd, at, bt, spanStart, oEnd, edits);
                oCur = oEnd;
                cCur = cEnd;
            }
        }
        // Whatever sits between the last unit and the next anchor (spaces only) may differ too.
        AddEdit(original, cleaned, oCur, oLimit, cCur, cLimit, [], [], oCur, oLimit, edits);
    }

    private static void AddEdit(string original, string cleaned, int os, int oe, int cs, int ce,
        Token[] at, Token[] bt, int spanStart, int spanEnd, List<TextEdit> edits)
    {
        string o = original[os..oe], c = cleaned[cs..ce];
        if (o == c) return;

        int p = 0;
        while (p < o.Length && p < c.Length && o[p] == c[p]) p++;
        int q = 0;
        while (q < o.Length - p && q < c.Length - p && o[o.Length - 1 - q] == c[c.Length - 1 - q]) q++;

        var ow = at.Where(t => t.IsWord).Select(t => t.Text).ToArray();
        var nw = bt.Where(t => t.IsWord).Select(t => t.Text).ToArray();
        edits.Add(new TextEdit(os + p, oe - q, c[p..(c.Length - q)], Classify(at, bt, ow, nw), ow, nw, spanStart, spanEnd));
    }

    private static EditKind Classify(Token[] at, Token[] bt, string[] ow, string[] nw)
    {
        if (at.Length == 0 && bt.Length == 0) return EditKind.Spacing;
        if (ow.Length == 0 && nw.Length == 0) return EditKind.Punctuation;
        if (ow.SequenceEqual(nw))
            return at.Select(t => t.Text).SequenceEqual(bt.Select(t => t.Text)) ? EditKind.Spacing : EditKind.Punctuation;
        if (ow.Length == 0) return EditKind.Insertion;
        if (nw.Length == 0) return EditKind.Deletion;
        if (string.Concat(ow.Select(Similarity.Key)) == string.Concat(nw.Select(Similarity.Key)))
        {
            if (ow.Length != nw.Length || !ow.Zip(nw).All(p => Similarity.Key(p.First) == Similarity.Key(p.Second)))
                return EditKind.Spacing;   // the letters stayed, the word boundaries moved
            return ow.Zip(nw).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase))
                ? EditKind.Capitalisation
                : EditKind.Apostrophe;
        }
        return EditKind.Substitution;
    }

    /// <summary>Index pairs of an exact longest common subsequence of token texts.</summary>
    private static List<(int A, int B)> Lcs(Token[] a, Token[] b)
    {
        int n = a.Length, m = b.Length;
        var len = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                len[i, j] = a[i].Text == b[j].Text ? len[i + 1, j + 1] + 1 : Math.Max(len[i + 1, j], len[i, j + 1]);

        var pairs = new List<(int, int)>();
        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (a[i].Text == b[j].Text) { pairs.Add((i, j)); i++; j++; }
            else if (len[i + 1, j] >= len[i, j + 1]) i++;
            else j++;
        }
        return pairs;
    }
}
