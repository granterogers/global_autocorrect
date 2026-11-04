namespace GlobalAutocorrect.Core.Write;

/// <summary>
/// D84, user report "strange artifacts... things are all uppercase": the last check before any fix is typed, on the
/// whole words it changes. Whatever the layer decided, a change that looks like a mistake is not made, and is remembered
/// so it is never tried again (MistakeMemory). Each rule names something a typo fix never needs to do.
/// </summary>
public static class WriteGuard
{
    /// <summary>Why the change is refused, or null when it looks like a fix.</summary>
    public static string? Problem(string before, string after)
    {
        if (after.Any(c => c is '￼' or '�' || (char.IsControl(c) && c is not ('\n' or '\t'))))
            return "odd-character";   // an embedded object or a broken character copied out of the field
        // A fix capitalises the first letter of a word (a sentence start, "i", a day or a name), never the letters inside
        // one: "teh" → "THE" or a sentence turned to capitals is an artifact. "iPhone" (one inside capital) can be a fix.
        // Writing in capitals already (most letters before are capitals) is the user's choice and left to the layers.
        var letters = before.Where(char.IsLetter).ToArray();
        bool shouting = letters.Length > 0 && letters.Count(char.IsUpper) * 10 >= letters.Length * 6;
        if (!shouting && (InsideCapitals(after) - InsideCapitals(before) >= 2 || AllCapitalWords(after) > AllCapitalWords(before))) return "capitals";
        if (DoubledWord(after) && !DoubledWord(before)) return "doubled-word";
        return null;
    }

    private static string[] Words(string s) => s.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
        .Select(w => w.Trim('.', ',', ';', ':', '!', '?', '"', '(', ')')).Where(w => w.Length > 0).ToArray();

    /// <summary>Capitals that aren't a word's first letter.</summary>
    private static int InsideCapitals(string s) => Words(s).Sum(w => w.Skip(1).Count(char.IsUpper));

    /// <summary>Words of two or more letters all in capitals ("THE", "UK").</summary>
    private static int AllCapitalWords(string s) =>
        Words(s).Count(w => w.Count(char.IsLetter) >= 2 && w.Where(char.IsLetter).All(char.IsUpper));

    private static bool DoubledWord(string s)
    {
        var w = s.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim('.', ',', ';', ':', '!', '?').ToLowerInvariant()).Where(x => x.Length > 0).ToArray();
        for (int i = 1; i < w.Length; i++)
            if (w[i] == w[i - 1] && w[i] is not ("had" or "that" or "is" or "very" or "bye")) return true;   // "had had", "that that"
        return false;
    }

    /// <summary>The whole words the change touches, before and after it, from the typed text.</summary>
    public static (string Before, string After) WholeWords(string typed, int start, int end, string replacement)
    {
        static bool Joins(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’';
        int s = start, e = end;
        while (s > 0 && Joins(typed[s - 1])) s--;
        while (e < typed.Length && Joins(typed[e])) e++;
        return (typed[s..e], typed[s..start] + replacement + typed[end..e]);
    }
}
