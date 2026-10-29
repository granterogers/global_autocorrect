namespace GlobalAutocorrect.Core.Text;

/// <summary>Finds sentence boundaries within one paragraph (4.4.a, 4.4.b).</summary>
public static class SentenceSplitter
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "st", "prof", "etc", "approx", "vs", "eg", "ie", "no", "dept", "est", "jan", "feb",
        "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
    };

    /// <summary>[Start, End) of each sentence; End is after its closing punctuation, or the text end for an unfinished one.</summary>
    public static IReadOnlyList<(int Start, int End)> Split(string text)
    {
        var result = new List<(int, int)>();
        int start = SkipSpace(text, 0);
        int i = start;
        while (i < text.Length)
        {
            if (text[i] is '.' or '!' or '?')
            {
                int end = i + 1;
                while (end < text.Length && text[end] is '.' or '!' or '?' or '"' or '\'' or ')' or '”' or '’') end++;
                bool atBoundary = end == text.Length || char.IsWhiteSpace(text[end]);
                if (atBoundary && !(text[i] == '.' && IsAbbreviation(text, i)))
                {
                    result.Add((start, end));
                    start = SkipSpace(text, end);
                    i = start;
                    continue;
                }
                i = end;
                continue;
            }
            i++;
        }
        if (start < text.Length) result.Add((start, text.Length));
        return result;
    }

    /// <summary>Offset where the last <paramref name="count"/> sentences begin.</summary>
    public static int StartOfLastSentences(string text, int count)
    {
        var s = Split(text);
        return s.Count == 0 ? text.Length : s[Math.Max(0, s.Count - count)].Start;
    }

    /// <summary>True when the text ends with sentence-ending punctuation followed by whitespace.</summary>
    public static bool EndsSentence(string text)
    {
        int i = text.Length - 1;
        if (i < 1 || !char.IsWhiteSpace(text[i])) return false;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        while (i >= 0 && text[i] is '"' or '\'' or ')' or '”' or '’') i--;
        return i >= 0 && text[i] is '.' or '!' or '?' && !(text[i] == '.' && IsAbbreviation(text, i));
    }

    private static int SkipSpace(string t, int i)
    {
        while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
        return i;
    }

    private static bool IsAbbreviation(string t, int dot)
    {
        int s = dot;
        while (s > 0 && (char.IsLetter(t[s - 1]) || t[s - 1] == '.')) s--;
        string w = t[s..dot].Replace(".", "", StringComparison.Ordinal);
        return (w.Length == 1 && char.IsLetter(w[0]) && dot - s == 1 && w != "I" && w != "a") || Abbreviations.Contains(w);
    }
}
