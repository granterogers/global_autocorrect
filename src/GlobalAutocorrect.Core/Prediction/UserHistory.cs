using System.Text;

namespace GlobalAutocorrect.Core.Prediction;

/// <summary>
/// The user's own typing history for prediction (7.3, D15, D40): word counts, and counts of the short word sequences
/// (two and three words, within one sentence) the user types, so their own phrasing is predicted ("how are you doing").
/// Never whole sentences, never from fields the app may not read (8.3). Stored as <c>words&lt;TAB&gt;count</c> lines
/// (one to three words separated by spaces; "&lt;s&gt;" marks a sentence start), UTF-8 without BOM. Thread-safe.
/// D57: the same counts are also kept per app (its process name), as <c>@app&lt;TAB&gt;words&lt;TAB&gt;count</c> lines after
/// the global ones, so prediction can prefer what the user typed in the app they're in. Older files (global lines only)
/// load as they are, and older versions of the app skip the per-app lines.
/// </summary>
public sealed class UserHistory
{
    private const int MaxWordLength = 30;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    // D40: "context" → (next word → count). Context is one word ("you") or two ("are you"); "<s>" is a sentence start.
    private readonly Dictionary<string, Dictionary<string, long>> _next = new(StringComparer.Ordinal);
    private const int MaxPhrases = 60_000;

    /// <summary>D57: one app's share of the counts above.</summary>
    private sealed class AppCounts
    {
        public readonly Dictionary<string, long> Words = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, long>> Next = new(StringComparer.Ordinal);
        public long Total;
    }

    private readonly Dictionary<string, AppCounts> _apps = new(StringComparer.Ordinal);
    private long _total;
    // D57: bounded so the file can't grow without limit: the most-used apps, and past the cap one-off entries go first.
    private const int MaxApps = 20;
    private const int MaxAppEntries = 60_000;
    private const int MaxAppNameLength = 40;

    public const string SentenceStart = "<s>";

    public UserHistory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        Load();
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GlobalAutocorrect",
        "history.txt");

    /// <summary>Counts one use of a word. Ignores tokens with digits, other non-letters, or more than 30 characters.</summary>
    public void Observe(string word) => Observe(word, null);

    /// <param name="app">D57: the app it was typed in (process name), or null when unknown; counted globally either way.</param>
    public void Observe(string word, string? app)
    {
        var key = Normalise(word);
        if (key is null)
        {
            return;
        }

        var appKey = AppKey(app);
        lock (_gate)
        {
            _counts[key] = _counts.GetValueOrDefault(key) + 1;
            _total++;
            if (appKey is not null)
            {
                var a = App(appKey);
                a.Words[key] = a.Words.GetValueOrDefault(key) + 1;
                a.Total++;
            }
        }
    }

    /// <summary>
    /// D40: counts that <paramref name="word"/> followed <paramref name="w2"/> (and <paramref name="w1"/>, w2) in one
    /// sentence. Pass <see cref="SentenceStart"/> for a word that starts a sentence.
    /// </summary>
    public void ObservePhrase(string? w1, string? w2, string word) => ObservePhrase(w1, w2, word, null);

    /// <param name="app">D57: the app it was typed in (process name), or null when unknown; counted globally either way.</param>
    public void ObservePhrase(string? w1, string? w2, string word, string? app)
    {
        var w = Normalise(word);
        var b = Context(w2);
        if (w is null || b is null) return;
        var a = w1 is null ? null : Context(w1);
        var appKey = AppKey(app);
        lock (_gate)
        {
            Bump(_next, b, w, 1);
            if (a is not null) Bump(_next, a + " " + b, w, 1);
            if (appKey is null) return;
            var per = App(appKey);
            Bump(per.Next, b, w, 1);
            if (a is not null) Bump(per.Next, a + " " + b, w, 1);
        }
    }

    /// <summary>D57: how often the user typed <paramref name="word"/> in <paramref name="app"/> (0 for an unknown app).</summary>
    public long CountIn(string? app, string word)
    {
        var appKey = AppKey(app);
        var key = Normalise(word);
        if (appKey is null || key is null) return 0;
        lock (_gate)
        {
            return _apps.TryGetValue(appKey, out var a) ? a.Words.GetValueOrDefault(key) : 0;
        }
    }

    /// <summary>
    /// D57: as <see cref="NextAfter(string?, string?, out bool)"/>, with how many of each word's uses were in
    /// <paramref name="app"/> after the same context.
    /// </summary>
    public IReadOnlyList<(string Word, long Count, long InApp)> NextAfter(string? w1, string? w2, string? app, out bool fromTwoWords)
    {
        fromTwoWords = false;
        var b = Context(w2);
        if (b is null) return [];
        var a = w1 is null ? null : Context(w1);
        var appKey = AppKey(app);
        lock (_gate)
        {
            string key;
            Dictionary<string, long>? all;
            if (a is not null && _next.TryGetValue(a + " " + b, out all)) { key = a + " " + b; fromTwoWords = true; }
            else if (_next.TryGetValue(b, out all)) key = b;
            else return [];
            Dictionary<string, long>? here = null;
            if (appKey is not null && _apps.TryGetValue(appKey, out var per)) per.Next.TryGetValue(key, out here);
            return all.Select(kv => (kv.Key, kv.Value, here?.GetValueOrDefault(kv.Key) ?? 0)).ToArray();
        }
    }

    /// <summary>D57: all word uses counted, and those in <paramref name="app"/> (0 for an unknown app).</summary>
    public (long All, long InApp) Totals(string? app)
    {
        var appKey = AppKey(app);
        lock (_gate) return (_total, appKey is not null && _apps.TryGetValue(appKey, out var a) ? a.Total : 0);
    }

    /// <summary>D57: the app names kept, lower case.</summary>
    public IReadOnlyList<string> Apps
    {
        get { lock (_gate) return _apps.Keys.ToArray(); }
    }

    private AppCounts App(string appKey)
    {
        if (!_apps.TryGetValue(appKey, out var a)) _apps[appKey] = a = new AppCounts();
        return a;
    }

    /// <summary>D57: a process name as stored: lower case letters, digits, '.', '-' and '_' only, at most 40 characters.</summary>
    private static string? AppKey(string? app)
    {
        if (string.IsNullOrWhiteSpace(app)) return null;
        var s = new string(app.Trim().ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_').ToArray());
        if (s.Length > MaxAppNameLength) s = s[..MaxAppNameLength];
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// D40: the user's own continuations after (<paramref name="w1"/>, <paramref name="w2"/>): the two-word context if
    /// the user has typed it, else the one-word context, else nothing.
    /// </summary>
    public IReadOnlyList<(string Word, long Count)> NextAfter(string? w1, string? w2) => NextAfter(w1, w2, out _);

    /// <param name="fromTwoWords">True when the continuations come from the two-word context, the more specific one.</param>
    public IReadOnlyList<(string Word, long Count)> NextAfter(string? w1, string? w2, out bool fromTwoWords)
    {
        fromTwoWords = false;
        var b = Context(w2);
        if (b is null) return [];
        var a = w1 is null ? null : Context(w1);
        lock (_gate)
        {
            if (a is not null && _next.TryGetValue(a + " " + b, out var tri)) { fromTwoWords = true; return tri.Select(kv => (kv.Key, kv.Value)).ToArray(); }
            return _next.TryGetValue(b, out var bi) ? bi.Select(kv => (kv.Key, kv.Value)).ToArray() : [];
        }
    }

    private static void Bump(Dictionary<string, Dictionary<string, long>> next, string context, string word, long by)
    {
        if (!next.TryGetValue(context, out var d)) next[context] = d = new(StringComparer.Ordinal);
        d[word] = d.GetValueOrDefault(word) + by;
    }

    private static string? Context(string? w) => w == SentenceStart ? w : Normalise(w);

    public long Count(string word)
    {
        var key = Normalise(word);
        if (key is null)
        {
            return 0;
        }

        lock (_gate)
        {
            return _counts.GetValueOrDefault(key);
        }
    }

    /// <summary>History words (lower-case) that start with <paramref name="prefix"/>, with their counts.</summary>
    public IReadOnlyList<(string Word, long Count)> WordsStartingWith(string prefix)
    {
        var result = new List<(string Word, long Count)>();
        lock (_gate)
        {
            foreach (var kv in _counts)
            {
                if (kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    result.Add((kv.Key, kv.Value));
                }
            }
        }

        return result;
    }

    /// <summary>Writes the counts atomically (temp file, then replace).</summary>
    public void Save()
    {
        string text;
        lock (_gate)
        {
            var sb = new StringBuilder();
            foreach (var kv in _counts.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            }

            // D40: phrases. Past the cap, one-off phrases go first.
            var phrases = _next.SelectMany(c => c.Value.Select(n => (Key: c.Key + " " + n.Key, Count: n.Value))).ToList();
            if (phrases.Count > MaxPhrases) phrases = phrases.OrderByDescending(p => p.Count).Take(MaxPhrases).ToList();
            foreach (var p in phrases.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.Append(p.Key).Append('\t').Append(p.Count).Append('\n');
            }

            // D57: per-app counts, within their caps (also trimmed in memory, so they stay bounded while running).
            PruneApps();
            foreach (var (app, a) in _apps.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                foreach (var kv in a.Words.OrderBy(k => k.Key, StringComparer.Ordinal))
                    sb.Append('@').Append(app).Append('\t').Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
                foreach (var p in a.Next.SelectMany(c => c.Value.Select(n => (Key: c.Key + " " + n.Key, Count: n.Value))).OrderBy(p => p.Key, StringComparer.Ordinal))
                    sb.Append('@').Append(app).Append('\t').Append(p.Key).Append('\t').Append(p.Count).Append('\n');
            }

            text = sb.ToString();
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>D57: keeps the <see cref="MaxApps"/> most-used apps, and at most <see cref="MaxAppEntries"/> per-app entries, commonest first.</summary>
    private void PruneApps()
    {
        if (_apps.Count > MaxApps)
        {
            foreach (var k in _apps.OrderByDescending(k => k.Value.Total).ThenBy(k => k.Key, StringComparer.Ordinal).Skip(MaxApps).Select(k => k.Key).ToList())
                _apps.Remove(k);
        }

        long entries = _apps.Values.Sum(a => a.Words.Count + a.Next.Values.Sum(d => (long)d.Count));
        if (entries <= MaxAppEntries) return;
        var all = _apps.SelectMany(a => a.Value.Words.Select(w => (Map: a.Value.Words, Key: w.Key, Count: w.Value))
                .Concat(a.Value.Next.SelectMany(c => c.Value.Select(n => (Map: c.Value, Key: n.Key, Count: n.Value)))))
            .OrderByDescending(e => e.Count).ThenBy(e => e.Key, StringComparer.Ordinal)
            .Skip(MaxAppEntries).ToList();
        foreach (var (map, key, _) in all) map.Remove(key);
        foreach (var a in _apps.Values)
            foreach (var c in a.Next.Where(c => c.Value.Count == 0).Select(c => c.Key).ToList()) a.Next.Remove(c);
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            foreach (var raw in File.ReadLines(_path, Encoding.UTF8))
            {
                // D57: "@app<TAB>" in front means the line is one app's share.
                var line = raw;
                string? appName = null;
                if (line.StartsWith('@'))
                {
                    int at = line.IndexOf('\t');
                    if (at <= 1 || AppKey(line[1..at]) is not { } name || name != line[1..at]) continue;
                    appName = name;
                    line = line[(at + 1)..];
                }

                int tab = line.IndexOf('\t');
                if (tab <= 0 || !long.TryParse(line.AsSpan(tab + 1), out long count) || count <= 0)
                {
                    continue;
                }

                var app = appName is null ? null : App(appName);
                var words = line[..tab].Split(' ');
                if (words.Length == 1)
                {
                    var key = Normalise(words[0]);
                    if (key is null) continue;
                    var map = app?.Words ?? _counts;
                    map[key] = map.GetValueOrDefault(key) + count;
                    if (app is not null) app.Total += count;
                    else _total += count;
                }
                else if (words.Length is 2 or 3 && words[..^1].All(x => Context(x) is not null) && Normalise(words[^1]) is { } last)
                {
                    Bump(app?.Next ?? _next, string.Join(' ', words[..^1].Select(x => Context(x)!)), last, count);   // D40
                }
            }
        }
        catch (IOException)
        {
            // An unreadable history file means starting empty.
        }
    }

    private static string? Normalise(string? word)
    {
        if (string.IsNullOrEmpty(word) || word.Length > MaxWordLength)
        {
            return null;
        }

        var lower = word.Replace('’', '\'').ToLowerInvariant();
        if (!char.IsLetter(lower[0]) || !char.IsLetter(lower[^1]))
        {
            return null;
        }

        foreach (char c in lower)
        {
            if (!char.IsLetter(c) && c != '\'')
            {
                return null;
            }
        }

        return lower;
    }
}
