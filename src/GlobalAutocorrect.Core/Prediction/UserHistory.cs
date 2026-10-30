using System.Text;

namespace GlobalAutocorrect.Core.Prediction;

/// <summary>
/// The user's own typing history for prediction (7.3, D15, D40): word counts, and counts of the short word sequences
/// (two and three words, within one sentence) the user types, so their own phrasing is predicted ("how are you doing").
/// Never whole sentences, never from fields the app may not read (8.3). Stored as <c>words&lt;TAB&gt;count</c> lines
/// (one to three words separated by spaces; "&lt;s&gt;" marks a sentence start), UTF-8 without BOM. Thread-safe.
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
    public void Observe(string word)
    {
        var key = Normalise(word);
        if (key is null)
        {
            return;
        }

        lock (_gate)
        {
            _counts[key] = _counts.GetValueOrDefault(key) + 1;
        }
    }

    /// <summary>
    /// D40: counts that <paramref name="word"/> followed <paramref name="w2"/> (and <paramref name="w1"/>, w2) in one
    /// sentence. Pass <see cref="SentenceStart"/> for a word that starts a sentence.
    /// </summary>
    public void ObservePhrase(string? w1, string? w2, string word)
    {
        var w = Normalise(word);
        var b = Context(w2);
        if (w is null || b is null) return;
        var a = w1 is null ? null : Context(w1);
        lock (_gate)
        {
            Bump(b, w, 1);
            if (a is not null) Bump(a + " " + b, w, 1);
        }
    }

    /// <summary>
    /// D40: the user's own continuations after (<paramref name="w1"/>, <paramref name="w2"/>): the two-word context if
    /// the user has typed it, else the one-word context, else nothing.
    /// </summary>
    public IReadOnlyList<(string Word, long Count)> NextAfter(string? w1, string? w2)
    {
        var b = Context(w2);
        if (b is null) return [];
        var a = w1 is null ? null : Context(w1);
        lock (_gate)
        {
            if (a is not null && _next.TryGetValue(a + " " + b, out var tri)) return tri.Select(kv => (kv.Key, kv.Value)).ToArray();
            return _next.TryGetValue(b, out var bi) ? bi.Select(kv => (kv.Key, kv.Value)).ToArray() : [];
        }
    }

    private void Bump(string context, string word, long by)
    {
        if (!_next.TryGetValue(context, out var d)) _next[context] = d = new(StringComparer.Ordinal);
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

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(_path, Encoding.UTF8))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0 || !long.TryParse(line.AsSpan(tab + 1), out long count) || count <= 0)
                {
                    continue;
                }

                var words = line[..tab].Split(' ');
                if (words.Length == 1)
                {
                    var key = Normalise(words[0]);
                    if (key is not null)
                    {
                        _counts[key] = _counts.GetValueOrDefault(key) + count;
                    }
                }
                else if (words.Length is 2 or 3 && words[..^1].All(x => Context(x) is not null) && Normalise(words[^1]) is { } last)
                {
                    Bump(string.Join(' ', words[..^1].Select(x => Context(x)!)), last, count);   // D40
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
