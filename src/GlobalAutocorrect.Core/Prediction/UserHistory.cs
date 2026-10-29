using System.Text;

namespace GlobalAutocorrect.Core.Prediction;

/// <summary>
/// The user's own typing history for prediction (7.3, D15): unigram word counts only. Pairs and sentences are
/// never kept, in memory or on disk (8.6). Stored as <c>word&lt;TAB&gt;count</c> lines, UTF-8 without BOM. Thread-safe.
/// </summary>
public sealed class UserHistory
{
    private const int MaxWordLength = 30;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);

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

                var key = Normalise(line[..tab]);
                if (key is not null)
                {
                    _counts[key] = _counts.GetValueOrDefault(key) + count;
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
