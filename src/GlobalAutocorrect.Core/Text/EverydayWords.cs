namespace GlobalAutocorrect.Core.Text;

/// <summary>
/// D133 (real-use run: "10 mins" became "10 mind"). Words people type every day that the en_GB dictionary doesn't list:
/// short forms, units and chat words. They are words, never typos, for every layer. And UK spellings the model turned
/// into US ones ("adaptor" → "adapter"): a correctly spelled word is never swapped for its other spelling.
/// </summary>
public static class EverydayWords
{
    /// <summary>D175: the brain's "Words.Known" list (defaults in <see cref="Learning.WordLists"/>).</summary>
    public static bool Contains(string word) => Learning.WordLists.Known.Contains(word);

    /// <summary>D175: UK ↔ US spelling pairs, the UK side first: the brain's "Words.SpellingPairs".</summary>
    private static IEnumerable<(string Typed, string Meant)> Pairs => Learning.WordLists.SpellingPairs.Pairs;

    /// <summary>Pairs that also look like typo fixes ("untill" → "until"): only for a word the dictionary knows ("travelled"). "Words.SpellingPairsKnownOnly".</summary>
    private static IEnumerable<(string Typed, string Meant)> KnownOnlyPairs => Learning.WordLists.SpellingPairsKnownOnly.Pairs;

    /// <summary>True when <paramref name="b"/> is <paramref name="a"/> spelled the other way (UK/US, either direction).</summary>
    public static bool SpellingVariant(string a, string b, bool aIsAWord = true)
    {
        a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
        if (a == b) return false;
        foreach (var (uk, us) in aIsAWord ? Pairs.Concat(KnownOnlyPairs) : Pairs)
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
