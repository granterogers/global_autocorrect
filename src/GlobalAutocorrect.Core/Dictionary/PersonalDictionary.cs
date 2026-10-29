using System.Text;

namespace GlobalAutocorrect.Core.Dictionary;

/// <summary>
/// The user's personal word list (8.5). Stored as plain UTF-8 text, one word per line (8.6).
/// Auto-learn counts live in memory only and are never written to disk (8.6).
/// </summary>
public sealed class PersonalDictionary
{
    private static readonly char[] TrimChars = ['\'', '"', '.', ',', '!', '?', ';', ':', '(', ')'];

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly HashSet<string> _words = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _unrecognisedCounts = new(StringComparer.OrdinalIgnoreCase);

    public PersonalDictionary(string filePath, int autoLearnThreshold = 3, bool autoLearnEnabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        AutoLearnThreshold = autoLearnThreshold;
        AutoLearnEnabled = autoLearnEnabled;
        Load();
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GlobalAutocorrect",
        "dictionary.txt");

    public bool AutoLearnEnabled { get; set; }

    public int AutoLearnThreshold { get; set; }

    public IReadOnlyList<string> Words
    {
        get
        {
            lock (_gate)
            {
                return SortedSnapshot();
            }
        }
    }

    public event Action? Changed;

    public bool Contains(string word)
    {
        var key = Normalise(word);
        if (key.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return _words.Contains(key);
        }
    }

    public bool Add(string word)
    {
        bool added;
        lock (_gate)
        {
            added = AddLocked(word);
        }

        if (added)
        {
            Changed?.Invoke();
        }

        return added;
    }

    public bool Remove(string word)
    {
        var key = Normalise(word);
        if (key.Length == 0)
        {
            return false;
        }

        bool removed;
        lock (_gate)
        {
            removed = _words.Remove(key);
            if (removed)
            {
                Save();
            }
        }

        if (removed)
        {
            Changed?.Invoke();
        }

        return removed;
    }

    /// <summary>Counts an unrecognised word in memory; adds it at the threshold when auto-learn is on.</summary>
    public bool NoteUnrecognised(string word)
    {
        var key = Normalise(word);
        if (!IsValidWord(key))
        {
            return false;
        }

        bool added = false;
        lock (_gate)
        {
            if (!AutoLearnEnabled || _words.Contains(key))
            {
                return false;
            }

            _unrecognisedCounts.TryGetValue(key, out var count);
            count++;
            if (count >= AutoLearnThreshold)
            {
                _unrecognisedCounts.Remove(key);
                added = AddLocked(key);
            }
            else
            {
                _unrecognisedCounts[key] = count;
            }
        }

        if (added)
        {
            Changed?.Invoke();
        }

        return added;
    }

    // Caller holds _gate.
    private bool AddLocked(string word)
    {
        var key = Normalise(word);
        if (!IsValidWord(key) || !_words.Add(key))
        {
            return false;
        }

        _unrecognisedCounts.Remove(key);
        Save();
        return true;
    }

    private static string Normalise(string? word) =>
        word is null ? string.Empty : word.Trim().Trim(TrimChars).Trim();

    private static bool IsValidWord(string key)
    {
        if (key.Length == 0 || key[0] == '#')
        {
            return false;
        }

        foreach (var c in key)
        {
            if (char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    private List<string> SortedSnapshot()
    {
        var list = new List<string>(_words);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            foreach (var line in File.ReadAllLines(_filePath, Encoding.UTF8))
            {
                var key = Normalise(line);
                if (IsValidWord(key))
                {
                    _words.Add(key);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable file leaves an empty dictionary; nothing is logged (8.6).
        }
    }

    // Caller holds _gate.
    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var sb = new StringBuilder();
            foreach (var word in SortedSnapshot())
            {
                sb.Append(word).Append('\n');
            }

            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory list stays authoritative for this session; nothing is logged (8.6).
        }
    }
}
