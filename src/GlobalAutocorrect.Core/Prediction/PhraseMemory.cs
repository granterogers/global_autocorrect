using System.Text.Json;

namespace GlobalAutocorrect.Core.Prediction;

/// <summary>
/// D142, user request "become aware of frequent things I type and then begin to anticipate them … I'm OK if that means
/// you record more of what is said … extra security to keep it unreadable by any other program". The phrases the user
/// repeats, so the rest of one can be offered after its first words (Tab takes it all).
/// <para>
/// Only repeats are ever kept: while typing, phrases of <see cref="MinWords"/>–<see cref="MaxWords"/> words are counted in
/// memory only, and one is kept once it was typed <see cref="MinUses"/> times on <see cref="MinDays"/> different days. A
/// one-off sentence never leaves memory. Phrases are made of plain words only: the caller breaks a phrase at anything
/// else (a number, link, email, @mention, unknown word, pasted text, another language), so none of those is ever kept.
/// Kept phrases unused for <see cref="ForgetAfterDays"/> days are forgotten. The kept list is saved encrypted by the
/// app (Windows DPAPI, this user only) and never leaves the PC: not in the brain, logs or reports.
/// </para>
/// </summary>
public sealed class PhraseMemory(Func<DateOnly> today)
{
    public const int MinWords = 3, MaxWords = 8, MinUses = 3, MinDays = 2, ForgetAfterDays = 90;
    public const int MaxCandidates = 20_000, MaxKept = 5_000;

    /// <summary>A continuation is shown only when at least this share of what followed these words before agrees.</summary>
    public const double AgreeShare = 0.7;

    private sealed class Entry
    {
        public string[] Words { get; set; } = [];
        public int Uses { get; set; }
        public int FirstDay { get; set; }
        public int LastDay { get; set; }
        public bool TwoDays { get; set; }
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _candidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _kept = new(StringComparer.Ordinal);
    private readonly List<string> _sentence = [];
    private bool _dirty;

    /// <summary>Setting "Learn phrases I repeat" (D142). Off: nothing is learned and nothing is offered.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True when the kept list changed since it was last saved.</summary>
    public bool Dirty { get { lock (_lock) return _dirty; } }

    private int Day => today().DayNumber;

    /// <summary>A plain word the user finished (as meant: after its instant fix).</summary>
    public void Word(string word)
    {
        if (!Enabled) return;
        lock (_lock)
        {
            _sentence.Add(word);
            if (_sentence.Count >= 40) EndLocked();
        }
    }

    /// <summary>The phrase stops here: a sentence end, a line break, a click, or anything that isn't a plain word.</summary>
    public void Break()
    {
        lock (_lock) EndLocked();
    }

    private void EndLocked()
    {
        if (_sentence.Count >= MinWords)
        {
            int day = Day;
            for (int i = 0; i + MinWords <= _sentence.Count; i++)
                Count(_sentence.Skip(i).Take(MaxWords).ToArray(), day);
        }
        _sentence.Clear();
    }

    private static string Key(IEnumerable<string> words) => string.Join(' ', words.Select(w => w.ToLowerInvariant()));

    private void Count(string[] words, int day)
    {
        string key = Key(words);
        if (_kept.TryGetValue(key, out var kept))
        {
            kept.Uses++;
            kept.LastDay = day;
            kept.Words = words;   // the latest way of writing it (capitals)
            _dirty = true;
            return;
        }
        if (!_candidates.TryGetValue(key, out var c))
        {
            if (_candidates.Count >= MaxCandidates) TrimCandidates();
            _candidates[key] = c = new Entry { Words = words, FirstDay = day, LastDay = day };
        }
        c.Uses++;
        if (day != c.FirstDay) c.TwoDays = true;
        c.LastDay = day;
        c.Words = words;
        if (c.Uses >= MinUses && c.TwoDays)
        {
            _candidates.Remove(key);
            if (_kept.Count >= MaxKept) TrimKept();
            _kept[key] = c;
            _dirty = true;
        }
    }

    private void TrimCandidates()
    {
        foreach (var k in _candidates.OrderBy(e => e.Value.LastDay).ThenBy(e => e.Value.Uses).Take(_candidates.Count / 4).Select(e => e.Key).ToList())
            _candidates.Remove(k);
    }

    private void TrimKept()
    {
        foreach (var k in _kept.OrderBy(e => e.Value.LastDay).ThenBy(e => e.Value.Uses).Take(_kept.Count / 10).Select(e => e.Key).ToList())
            _kept.Remove(k);
    }

    /// <summary>
    /// The rest of a phrase the user has repeated, to show after the caret, or null. <paramref name="lineBeforeCaret"/>
    /// is the current line up to the caret: its sentence's last words (and the word being typed, if any) must match the
    /// start of kept phrases, and what followed them before must agree (<see cref="AgreeShare"/>) word by word. At least
    /// two words are offered; a single word is the word predictor's job.
    /// </summary>
    public string? Continue(string lineBeforeCaret)
    {
        if (!Enabled) return null;
        int cut = lineBeforeCaret.LastIndexOfAny(['.', '!', '?', '\n']);
        string sentence = lineBeforeCaret[(cut + 1)..];
        bool endsInSpace = sentence.Length > 0 && char.IsWhiteSpace(sentence[^1]);
        var parts = sentence.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        string partial = endsInSpace ? "" : parts[^1];
        var context = (endsInSpace ? parts : parts[..^1]).Select(Plain).ToArray();
        if (context.Any(w => w is null) || (partial.Length > 0 && Plain(partial) is null)) return null;
        // At least two words of this sentence before, or one when it is the sentence's only word so far ("Let" → "me know…").
        if (context.Length == 0) return null;

        lock (_lock)
        {
            for (int k = Math.Min(4, context.Length); k >= 1; k--)
            {
                if (k < 2 && context.Length > 1) break;
                var last = context[^k..].Select(w => w!.ToLowerInvariant()).ToArray();
                var matches = _kept.Values.Where(e => e.Words.Length > k && e.Words.Take(k).Select(w => w.ToLowerInvariant()).SequenceEqual(last)
                                                      && (partial.Length == 0 || e.Words[k].StartsWith(partial, StringComparison.OrdinalIgnoreCase)
                                                                              && e.Words[k].Length > partial.Length)).ToList();
                if (matches.Count == 0) continue;
                var words = new List<string>();
                var alive = matches;
                for (int pos = k; pos < MaxWords; pos++)
                {
                    int total = alive.Sum(e => e.Uses);
                    var top = alive.Where(e => e.Words.Length > pos).GroupBy(e => e.Words[pos].ToLowerInvariant())
                                   .Select(g => (Word: g.OrderByDescending(e => e.Uses).First().Words[pos], Uses: g.Sum(e => e.Uses), Entries: g.ToList()))
                                   .OrderByDescending(g => g.Uses).FirstOrDefault();
                    if (top.Entries is null || top.Uses < MinUses || top.Uses < AgreeShare * total) break;
                    words.Add(top.Word);
                    alive = top.Entries;
                }
                if (words.Count < 2) return null;
                string first = partial.Length > 0 ? words[0][partial.Length..] : words[0];
                return first + " " + string.Join(' ', words.Skip(1));
            }
        }
        return null;
    }

    /// <summary>Only letters and apostrophes, with a comma or similar at the end: a plain word, as phrases are made of.</summary>
    private static string? Plain(string w)
    {
        string core = w.TrimEnd(',', ';', ':');
        return core.Length > 0 && core.All(c => char.IsLetter(c) || c is '\'' or '’' or '-') ? core : null;
    }

    /// <summary>What has been kept, most used first: for Settings, where the user can see and remove any of it.</summary>
    public IReadOnlyList<(string Phrase, int Uses)> Kept
    {
        get { lock (_lock) return _kept.Values.OrderByDescending(e => e.Uses).Select(e => (string.Join(' ', e.Words), e.Uses)).ToList(); }
    }

    /// <summary>Forgets a kept phrase, and every kept phrase it is part of.</summary>
    public void Forget(string phrase)
    {
        string key = Key(phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        lock (_lock)
        {
            foreach (var k in _kept.Keys.Where(k => k.Contains(key, StringComparison.Ordinal)).ToList()) _kept.Remove(k);
            foreach (var k in _candidates.Keys.Where(k => k.Contains(key, StringComparison.Ordinal)).ToList()) _candidates.Remove(k);
            _dirty = true;
        }
    }

    public void ForgetAll()
    {
        lock (_lock) { _kept.Clear(); _candidates.Clear(); _sentence.Clear(); _dirty = true; }
    }

    private sealed record Saved(string[] Words, int Uses, int LastDay);

    /// <summary>The kept phrases (never the candidates), for the app to encrypt and save.</summary>
    public byte[] Save()
    {
        lock (_lock)
        {
            _dirty = false;
            return JsonSerializer.SerializeToUtf8Bytes(_kept.Values.Select(e => new Saved(e.Words, e.Uses, e.LastDay)).ToList());
        }
    }

    /// <summary>Loads kept phrases saved by <see cref="Save"/>, forgetting those unused for <see cref="ForgetAfterDays"/>.</summary>
    public void Load(byte[] data)
    {
        var list = JsonSerializer.Deserialize<List<Saved>>(data) ?? [];
        int day = Day;
        lock (_lock)
        {
            _kept.Clear();
            foreach (var s in list.Where(s => s.Words is { Length: >= MinWords } && day - s.LastDay <= ForgetAfterDays).Take(MaxKept))
                _kept[Key(s.Words)] = new Entry { Words = s.Words, Uses = s.Uses, LastDay = s.LastDay, FirstDay = s.LastDay, TwoDays = true };
        }
    }
}
