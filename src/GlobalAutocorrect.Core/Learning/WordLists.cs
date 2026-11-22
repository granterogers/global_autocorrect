using System.Collections.Frozen;

namespace GlobalAutocorrect.Core.Learning;

/// <summary>
/// D175, user request ("the whole point is that it should be updating the brain ... the code itself like something that's
/// dynamic and improves without the actual binaries needing to change"): the language knowledge the correction layers use,
/// as named word lists the brain can add to and take from (<see cref="BrainFile.Words"/>). The defaults here are what a
/// build ships with; a brain's change goes out to every running copy within 15 minutes, with no release. Data only: each
/// entry is a short word or a "typed=meant" pair, checked before use.
/// </summary>
public sealed class WordList
{
    private FrozenSet<string> _set;
    private FrozenDictionary<string, string> _pairs;

    internal WordList(string name, string why, bool isPairs, StringComparer comparer, string[] defaults)
    {
        Name = name; Why = why; IsPairs = isPairs; Comparer = comparer; Defaults = defaults;
        (_set, _pairs) = Build(defaults);
    }

    public string Name { get; }
    public string Why { get; }
    /// <summary>Entries are "typed=meant" pairs (a word on each side), not single words.</summary>
    public bool IsPairs { get; }
    public StringComparer Comparer { get; }
    public IReadOnlyList<string> Defaults { get; }

    /// <summary>Every entry now in use (for a pair list, the "typed=meant" pairs).</summary>
    public IReadOnlyCollection<string> Items => Volatile.Read(ref _set);

    public bool Contains(string word) => Volatile.Read(ref _set).Contains(word);

    /// <summary>A pair list's meant word for <paramref name="typed"/>.</summary>
    public bool TryGet(string typed, out string meant) => Volatile.Read(ref _pairs).TryGetValue(typed, out meant!);

    /// <summary>A pair list's pairs, in order (for lists where order matters).</summary>
    public IEnumerable<(string Typed, string Meant)> Pairs => Volatile.Read(ref _set).Select(Split).Where(p => p.Meant.Length > 0);

    internal void Set(BrainWords? change)
    {
        var (set, pairs) = Build(change is null ? Defaults : Defaults.Where(d => !Removed(change, d)).Concat(change.Add ?? []));
        Volatile.Write(ref _pairs, pairs);
        Volatile.Write(ref _set, set);
    }

    private bool Removed(BrainWords change, string entry) =>
        (change.Remove ?? []).Any(r => Comparer.Equals(r, entry) || (IsPairs && Comparer.Equals(r, Split(entry).Typed)));

    private (FrozenSet<string>, FrozenDictionary<string, string>) Build(IEnumerable<string> entries)
    {
        var keep = entries.Where(WordLists.EntryOk).Where(e => !IsPairs || e.Contains('=')).ToList();
        var pairs = new Dictionary<string, string>(Comparer);
        if (IsPairs) foreach (var (t, m) in keep.Select(Split)) pairs[t] = m;   // a later entry (the brain's) wins
        if (IsPairs) keep = pairs.Select(p => p.Key + "=" + p.Value).ToList();
        return (keep.ToFrozenSet(Comparer), pairs.ToFrozenDictionary(Comparer));
    }

    private static (string Typed, string Meant) Split(string e)
    {
        int i = e.IndexOf('=');
        return i <= 0 ? (e, "") : (e[..i].Trim(), e[(i + 1)..].Trim());
    }
}

/// <summary>D175: a brain's change to one list: entries added, entries (or a pair's typed side) taken out.</summary>
public sealed record BrainWords
{
    public List<string>? Add { get; init; }
    public List<string>? Remove { get; init; }
}

public static class WordLists
{
    private static readonly Dictionary<string, WordList> ByName = new(StringComparer.OrdinalIgnoreCase);

    private static WordList Define(string name, string why, string[] defaults, bool pairs = false, bool caseSensitive = false)
    {
        var l = new WordList(name, why, pairs, caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase, defaults);
        ByName[name] = l;
        return l;
    }

    /// <summary>The most entries a brain may give one list.</summary>
    public const int MaxEntries = 5000;

    /// <summary>An entry a list can hold: 1–64 characters, no control characters, at most one "=" (a pair).</summary>
    public static bool EntryOk(string? e) =>
        e is { Length: >= 1 and <= 64 } && !e.Any(char.IsControl) && e.Count(c => c == '=') <= 1 && e.Trim().Length == e.Length
        && (e.IndexOf('=') is var i && (i < 0 || (i > 0 && i < e.Length - 1)));

    // ---- Words every layer treats as words ----
    public static readonly WordList Known = Define("Words.Known", "D133: words the en_GB dictionary doesn't list (short forms, units, chat words): never typos, for every layer", [
        "min", "mins", "hr", "hrs", "sec", "secs", "wk", "wks", "yr", "yrs", "mo", "mos", "approx", "appt", "appts", "dept",
        "mgr", "qty", "pcs", "pc", "kg", "km", "ml", "mb", "gb", "tb", "kb", "mph", "kph", "pls", "plz", "thx", "ty", "np",
        "btw", "fyi", "asap", "eta", "tbh", "imo", "imho", "idk", "wfh", "ooo", "lol", "lmao", "omg", "tbc", "tbd", "brb",
        "msg", "msgs", "txt", "pic", "pics", "convo", "info", "admin", "ok", "okay", "hmm", "hmmm", "haha", "hahaha", "xx",
        "xxx", "xo", "gonna", "wanna", "gotta", "dunno", "kinda", "sorta", "ya", "yep", "nope", "yeah", "cos", "cuz",
    ]);

    public static readonly WordList NeverChange = Define("Words.NeverChange", "D175: words no layer ever changes, as typed (names, product words)", [], caseSensitive: true);

    // ---- The instant layer ----
    public static readonly WordList InstantFixes = Define("Instant.Fixes", "D175: typed=meant pairs the instant layer fixes the moment the word ends (only words the dictionary doesn't know)", [], pairs: true);

    public static readonly WordList InstantLeave = Define("Instant.Leave", "D175: typed words the instant layer leaves to the AI, which sees the sentence (a guess between very different words)", []);

    // ---- The correction rules (4.5) ----
    public static readonly WordList Function = Define("Words.Function", "4.5.b: short function words the AI may add or drop", [
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "am", "to", "of", "and", "or", "in", "on", "at",
        "for", "with", "it", "that", "this", "i", "but", "as", "by", "do", "does", "did", "have", "has", "had", "not",
        "so", "if", "from", "up", "out",
    ]);

    public static readonly WordList SpellingPairs = Define("Words.SpellingPairs", "D133: UK=US spelling endings; a correctly spelled word is never swapped for its other spelling", [
        "our=or", "ise=ize", "isa=iza", "yse=yze", "tre=ter", "ogue=og", "ence=ense", "or=er", "grey=gray", "tyre=tire",
    ], pairs: true, caseSensitive: true);

    public static readonly WordList SpellingPairsKnownOnly = Define("Words.SpellingPairsKnownOnly", "D133: UK=US pairs that also look like typo fixes (\"untill\"), only for a word the dictionary knows", [
        "ll=l", "mme=m", "que=ck",
    ], pairs: true, caseSensitive: true);

    // ---- Sentences and tidying ----
    public static readonly WordList SentenceAbbreviations = Define("Sentence.Abbreviations", "4.4.a: words whose full stop doesn't end a sentence (for the AI's context)", [
        "mr", "mrs", "ms", "dr", "st", "prof", "etc", "approx", "vs", "eg", "ie", "no", "dept", "est", "jan", "feb",
        "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
    ]);

    public static readonly WordList TidyAbbreviations = Define("Tidy.Abbreviations", "D55: words after which the next word doesn't get a sentence capital", [
        "mr", "mrs", "ms", "dr", "st", "no", "vs", "etc", "approx", "fig", "cf", "al", "inc", "ltd", "co", "dept", "est",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec", "mon", "tue", "wed", "thu", "fri",
    ]);

    // ---- Language ----
    public static readonly WordList English = Define("Lang.English", "D116: common English words that make a sentence English (other languages get no fixes)", [
        "a", "the", "and", "to", "of", "in", "is", "it", "you", "that", "for", "on", "with", "this", "are", "be", "was", "have",
        "i", "we", "not", "but", "at", "your", "my", "can", "will", "do", "so", "if", "as", "all", "just", "from", "they",
        "or", "an", "our", "what", "there", "would", "about", "know", "please", "thanks", "hi", "hello", "dear", "regards",
        "could", "should", "been", "had", "has", "were", "which", "when", "how", "any", "some", "them", "their", "these",
        "those", "into", "than", "then", "also", "very", "here", "today", "tomorrow", "let", "get", "got", "it's", "i'm",
        "don't", "can't", "we're", "you're", "that's", "there's", "i'll", "we'll", "thank", "best", "kind",
    ]);

    /// <summary>Every list, by name.</summary>
    public static IReadOnlyCollection<WordList> All => ByName.Values;

    public static bool TryGet(string name, out WordList list) => ByName.TryGetValue(name, out list!);

    /// <summary>A brain's change to a list this app knows, with entries it can hold (see <see cref="EntryOk"/>).</summary>
    public static bool ChangeOk(string name, BrainWords? change) =>
        ByName.TryGetValue(name, out var l) && change is not null
        && (change.Add ?? []).Count + (change.Remove ?? []).Count <= MaxEntries
        && (change.Add ?? []).All(e => EntryOk(e) && (!l.IsPairs || e.Contains('=')))
        && (change.Remove ?? []).All(EntryOk);

    /// <summary>Use a brain's word lists: each list it changes, changed; every other back to its defaults.</summary>
    public static void Apply(IReadOnlyDictionary<string, BrainWords>? words)
    {
        foreach (var l in ByName.Values)
            l.Set(words is not null && words.TryGetValue(l.Name, out var w) ? w : null);
    }
}
