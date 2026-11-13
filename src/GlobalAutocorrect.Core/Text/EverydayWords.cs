namespace GlobalAutocorrect.Core.Text;

/// <summary>
/// D133 (real-use run: "10 mins" became "10 mind"). Words people type every day that the en_GB dictionary doesn't list:
/// short forms, units and chat words. They are words, never typos, for every layer. And UK spellings the model turned
/// into US ones ("adaptor" → "adapter"): a correctly spelled word is never swapped for its other spelling.
/// </summary>
public static class EverydayWords
{
    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "min", "mins", "hr", "hrs", "sec", "secs", "wk", "wks", "yr", "yrs", "mo", "mos", "approx", "appt", "appts", "dept",
        "mgr", "qty", "pcs", "pc", "kg", "km", "ml", "mb", "gb", "tb", "kb", "mph", "kph", "pls", "plz", "thx", "ty", "np",
        "btw", "fyi", "asap", "eta", "tbh", "imo", "imho", "idk", "wfh", "ooo", "lol", "lmao", "omg", "tbc", "tbd", "brb",
        "msg", "msgs", "txt", "pic", "pics", "convo", "info", "admin", "ok", "okay", "hmm", "hmmm", "haha", "hahaha", "xx",
        "xxx", "xo", "gonna", "wanna", "gotta", "dunno", "kinda", "sorta", "ya", "yep", "nope", "yeah", "cos", "cuz",
    };

    public static bool Contains(string word) => Words.Contains(word);

    /// <summary>UK ↔ US spelling pairs, the UK side first.</summary>
    private static readonly (string Uk, string Us)[] Pairs =
    [
        ("our", "or"), ("ise", "ize"), ("isa", "iza"), ("yse", "yze"), ("tre", "ter"), ("ogue", "og"), ("ence", "ense"),
        ("or", "er"), ("ll", "l"), ("ae", "e"), ("oe", "e"), ("mme", "m"), ("que", "ck"), ("grey", "gray"), ("tyre", "tire"),
    ];

    /// <summary>True when <paramref name="b"/> is <paramref name="a"/> spelled the other way (UK/US, either direction).</summary>
    public static bool SpellingVariant(string a, string b)
    {
        a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
        if (a == b) return false;
        foreach (var (uk, us) in Pairs)
            if (OneSwap(a, uk, us) == b || OneSwap(a, us, uk) == b) return true;
        return false;
    }

    private static string? OneSwap(string s, string from, string to)
    {
        for (int i = s.IndexOf(from, StringComparison.Ordinal); i >= 0; i = s.IndexOf(from, i + 1, StringComparison.Ordinal))
        {
            // Spelling differences come after a word's start (col-our, organ-ise, adapt-or); "for" → "four" is a real fix.
            if (i < 3 && from is not ("grey" or "gray" or "tyre" or "tire")) continue;
            string r = s[..i] + to + s[(i + from.Length)..];
            if (r != s) return r;
        }
        return null;
    }
}
