namespace GlobalAutocorrect.Core.Text;

public static class Similarity
{
    /// <summary>Optimal-string-alignment Damerau–Levenshtein distance (adjacent swaps cost 1).</summary>
    public static int DamerauLevenshtein(string a, string b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, d[i - 2, j - 2] + 1);
                d[i, j] = v;
            }
        }
        return d[n, m];
    }

    /// <summary>Distance divided by the longer length: 0 = identical, 1 = nothing in common (4.5.a).</summary>
    public static double NormDist(string a, string b)
    {
        int max = Math.Max(a.Length, b.Length);
        return max == 0 ? 0 : (double)DamerauLevenshtein(a, b) / max;
    }

    /// <summary>Lower-case and drop apostrophes, so case and apostrophe fixes compare as equal.</summary>
    public static string Key(string word) =>
        string.Concat(word.Where(c => c is not ('\'' or '’'))).ToLowerInvariant();
}
