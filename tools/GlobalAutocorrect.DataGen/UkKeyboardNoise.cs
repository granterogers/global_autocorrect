using GlobalAutocorrect.Core.Text;

namespace GlobalAutocorrect.DataGen;

public enum NoiseLevel { None, Light, Medium, Heavy }

public sealed record NoiseResult(string Noisy, IReadOnlyList<string> Ops);

/// <summary>
/// Deterministic (given the Random) generator of realistic UK-keyboard typing noise (6.1.b).
/// Protected tokens (numbers, URLs, emails, code-like words, including pound amounts) are never altered.
/// </summary>
public sealed class UkKeyboardNoise
{
    private static readonly (string Name, int Weight)[] OpTable =
    [
        ("neighbour", 20), ("swap", 15), ("drop", 15), ("double", 10), ("merge", 10),
        ("split", 5), ("misplace", 5), ("apostrophe", 8), ("lowercase", 7), ("grammar", 5),
    ];

    private static readonly int TotalWeight = OpTable.Sum(o => o.Weight);

    /// <summary>
    /// UK QWERTY adjacency for a-z: same-row left/right plus the two keys touching in each neighbouring row
    /// (rows are staggered, so each key touches two keys above and two below). Example: g -> f,h,t,y,v,b.
    /// </summary>
    private static readonly IReadOnlyDictionary<char, string> Neighbours = BuildNeighbours();

    private readonly Random _rng;

    public UkKeyboardNoise(Random rng) => _rng = rng ?? throw new ArgumentNullException(nameof(rng));

    /// <summary>Lower-case neighbour keys of a letter (empty for non-letters).</summary>
    public static string NeighboursOf(char letter) =>
        Neighbours.TryGetValue(char.ToLowerInvariant(letter), out var n) ? n : "";

    public NoiseResult Apply(string clean, NoiseLevel level)
    {
        if (level == NoiseLevel.None || string.IsNullOrEmpty(clean)) return new NoiseResult(clean, []);

        int w = Tokeniser.Tokenise(clean).Count(t => t.IsWord);
        int n = level switch
        {
            NoiseLevel.Light => Math.Max(1, Round(w * 0.07)),
            NoiseLevel.Medium => Math.Max(2, Round(w * 0.15)),
            NoiseLevel.Heavy => Math.Max(3, Round(w * 0.28)),
            _ => 0,
        };

        string text = clean;
        var ops = new List<string>();
        for (int i = 0; i < n; i++)
        {
            for (int tries = 0; tries < 20; tries++)
            {
                string name = PickOp();
                string? result = TryOp(name, text);
                if (result is null) continue;
                text = result;
                ops.Add(name);
                break;
            }
        }
        return new NoiseResult(text, ops);
    }

    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    private string PickOp()
    {
        int r = _rng.Next(TotalWeight);
        foreach (var (name, weight) in OpTable)
        {
            if (r < weight) return name;
            r -= weight;
        }
        return OpTable[0].Name;
    }

    private string? TryOp(string name, string text)
    {
        var tokens = Tokeniser.Tokenise(text);
        return name switch
        {
            "drop" => Drop(text, tokens),
            "double" => Double(text, tokens),
            "swap" => Swap(text, tokens),
            "neighbour" => Neighbour(text, tokens),
            "merge" => Merge(text, tokens),
            "split" => Split(text, tokens),
            "misplace" => Misplace(text, tokens),
            "apostrophe" => Apostrophe(text, tokens),
            "lowercase" => Lowercase(text, tokens),
            "grammar" => Grammar(text, tokens),
            _ => null,
        };
    }

    // ---- helpers ----

    private static bool Editable(Token t) => t.IsWord && !t.IsProtected;

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool LetterAt(string text, int i) => i >= 0 && i < text.Length && IsAsciiLetter(text[i]);

    private T Pick<T>(IReadOnlyList<T> items) => items[_rng.Next(items.Count)];

    /// <summary>Picks a random editable word passing <paramref name="wordOk"/>, then a random position in it passing <paramref name="posOk"/>.</summary>
    private int? PickLetterPosition(string text, IReadOnlyList<Token> tokens, Func<Token, bool> wordOk, Func<string, int, bool> posOk)
    {
        var words = new List<List<int>>();
        foreach (var t in tokens)
        {
            if (!Editable(t) || !wordOk(t)) continue;
            var positions = new List<int>();
            for (int i = t.Start; i < t.End; i++) if (posOk(text, i)) positions.Add(i);
            if (positions.Count > 0) words.Add(positions);
        }
        if (words.Count == 0) return null;
        return Pick(Pick(words));
    }

    private static string Replace(string text, int index, int length, string replacement) =>
        string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + length));

    private static string MatchCase(string original, string replacement) =>
        char.IsUpper(original[0]) ? char.ToUpperInvariant(replacement[0]) + replacement[1..] : replacement;

    // ---- ops ----

    private string? Drop(string text, IReadOnlyList<Token> tokens)
    {
        int? p = PickLetterPosition(text, tokens, t => t.Length >= 2, (s, i) => IsAsciiLetter(s[i]));
        return p is null ? null : text.Remove(p.Value, 1);
    }

    private string? Double(string text, IReadOnlyList<Token> tokens)
    {
        int? p = PickLetterPosition(text, tokens, _ => true, (s, i) => IsAsciiLetter(s[i]));
        return p is null ? null : text.Insert(p.Value, text[p.Value].ToString());
    }

    private string? Swap(string text, IReadOnlyList<Token> tokens)
    {
        int? p = PickLetterPosition(text, tokens, t => t.Length >= 2,
            (s, i) => LetterAt(s, i) && LetterAt(s, i + 1) && char.ToLowerInvariant(s[i]) != char.ToLowerInvariant(s[i + 1]));
        if (p is null) return null;
        int i = p.Value;
        char a = text[i], b = text[i + 1];
        // Keep the capitalisation pattern in place so "Hello" does not become "eHllo" (an inner capital).
        char na = char.IsUpper(a) ? char.ToUpperInvariant(b) : char.ToLowerInvariant(b);
        char nb = char.IsUpper(b) ? char.ToUpperInvariant(a) : char.ToLowerInvariant(a);
        return Replace(text, i, 2, new string([na, nb]));
    }

    private string? Neighbour(string text, IReadOnlyList<Token> tokens)
    {
        int? p = PickLetterPosition(text, tokens, _ => true, (s, i) => IsAsciiLetter(s[i]));
        if (p is null) return null;
        char c = text[p.Value];
        string options = NeighboursOf(c);
        if (options.Length == 0) return null;
        char r = options[_rng.Next(options.Length)];
        return Replace(text, p.Value, 1, (char.IsUpper(c) ? char.ToUpperInvariant(r) : r).ToString());
    }

    /// <summary>Indexes i where tokens[i] is an editable word, tokens[i+1] is a single space and tokens[i+2] is an editable word.</summary>
    private static List<int> WordSpaceWordPairs(IReadOnlyList<Token> tokens)
    {
        var list = new List<int>();
        for (int i = 0; i + 2 < tokens.Count; i++)
            if (Editable(tokens[i]) && tokens[i + 1].Kind == TokenKind.Space && tokens[i + 1].Text == " " && Editable(tokens[i + 2]))
                list.Add(i);
        return list;
    }

    private string? Merge(string text, IReadOnlyList<Token> tokens)
    {
        var pairs = WordSpaceWordPairs(tokens);
        if (pairs.Count == 0) return null;
        return text.Remove(tokens[Pick(pairs) + 1].Start, 1);
    }

    private string? Misplace(string text, IReadOnlyList<Token> tokens)
    {
        var pairs = WordSpaceWordPairs(tokens);
        if (pairs.Count == 0) return null;
        int i = Pick(pairs);
        Token first = tokens[i], second = tokens[i + 2];
        int sp = tokens[i + 1].Start;
        var dirs = new List<int>();
        if (first.Length >= 2 && LetterAt(text, sp - 1)) dirs.Add(-1);
        if (second.Length >= 2 && LetterAt(text, sp + 1)) dirs.Add(1);
        if (dirs.Count == 0) return null;
        // "the cat" -> "th ecat" (left) or "thec at" (right)
        return Pick(dirs) < 0
            ? string.Concat(text.AsSpan(0, sp - 1), " ", text.AsSpan(sp - 1, 1), text.AsSpan(sp + 1))
            : string.Concat(text.AsSpan(0, sp), text.AsSpan(sp + 1, 1), " ", text.AsSpan(sp + 2));
    }

    private string? Split(string text, IReadOnlyList<Token> tokens)
    {
        var candidates = new List<int>();
        foreach (var t in tokens)
        {
            if (!Editable(t) || t.Length < 5) continue;
            for (int k = t.Start + 1; k < t.End; k++)
                if (LetterAt(text, k - 1) && LetterAt(text, k)) candidates.Add(k);
        }
        if (candidates.Count == 0) return null;
        return text.Insert(Pick(candidates), " ");
    }

    private string? Apostrophe(string text, IReadOnlyList<Token> tokens)
    {
        var positions = new List<int>();
        foreach (var t in tokens)
        {
            if (!Editable(t)) continue;
            for (int i = t.Start; i < t.End; i++) if (text[i] is '\'' or '’') positions.Add(i);
        }
        return positions.Count == 0 ? null : text.Remove(Pick(positions), 1);
    }

    private string? Lowercase(string text, IReadOnlyList<Token> tokens)
    {
        var candidates = new List<Token>();
        bool sentenceStart = true;
        foreach (var t in tokens)
        {
            if (t.Kind == TokenKind.Newline) { sentenceStart = true; continue; }
            if (t.Kind == TokenKind.Space) continue;
            if (t.Kind == TokenKind.Punct)
            {
                if (t.Text is "." or "!" or "?") sentenceStart = true;
                continue;
            }
            bool isI = t.Text == "I" || t.Text.StartsWith("I'", StringComparison.Ordinal) || t.Text.StartsWith("I’", StringComparison.Ordinal);
            if (Editable(t) && char.IsAsciiLetterUpper(t.Text[0]) && (t.Length == 1 || !char.IsUpper(t.Text[1])) && (sentenceStart || isI))
                candidates.Add(t);
            sentenceStart = false;
        }
        if (candidates.Count == 0) return null;
        var pick = Pick(candidates);
        return Replace(text, pick.Start, 1, char.ToLowerInvariant(text[pick.Start]).ToString());
    }

    private string? Grammar(string text, IReadOnlyList<Token> tokens)
    {
        var options = new List<(int Start, int Length, string Replacement)>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (!Editable(t)) continue;
            string lower = t.Text.ToLowerInvariant().Replace('’', '\'');
            string? repl = lower switch
            {
                "its" => "it's",
                "it's" => "its",
                "their" => "there",
                "there" => "their",
                "your" => "you're",
                "you're" => "your",
                _ => null,
            };
            if (repl is not null) options.Add((t.Start, t.Length, MatchCase(t.Text, repl)));

            if (lower is "a" or "an" && i + 2 < tokens.Count && tokens[i + 1].Kind == TokenKind.Space && Editable(tokens[i + 2]))
            {
                char next = char.ToLowerInvariant(tokens[i + 2].Text[0]);
                bool vowel = next is 'a' or 'e' or 'i' or 'o' or 'u';
                if (char.IsLetter(next))
                {
                    if (lower == "a" && !vowel) options.Add((t.Start, t.Length, MatchCase(t.Text, "an")));
                    else if (lower == "an" && vowel) options.Add((t.Start, t.Length, MatchCase(t.Text, "a")));
                }
            }

            if (t.Text == "the" && i + 2 < tokens.Count && tokens[i + 1].Kind == TokenKind.Space && tokens[i + 2].IsWord)
                options.Add((t.Start, tokens[i + 1].End - t.Start, ""));
        }
        if (options.Count == 0) return null;
        var o = Pick(options);
        return Replace(text, o.Start, o.Length, o.Replacement);
    }

    private static IReadOnlyDictionary<char, string> BuildNeighbours()
    {
        string[] rows = ["qwertyuiop", "asdfghjkl", "zxcvbnm"];
        var map = new Dictionary<char, string>();
        for (int r = 0; r < rows.Length; r++)
        {
            for (int c = 0; c < rows[r].Length; c++)
            {
                var n = new List<char>();
                if (c > 0) n.Add(rows[r][c - 1]);
                if (c < rows[r].Length - 1) n.Add(rows[r][c + 1]);
                // The home row sits half a key right of the top row, and the bottom row half a key right of the home row,
                // so a key touches c and c+1 in the row above, and c-1 and c in the row below.
                if (r > 0) AddIfValid(n, rows[r - 1], c, c + 1);
                if (r < rows.Length - 1) AddIfValid(n, rows[r + 1], c - 1, c);
                map[rows[r][c]] = new string(n.ToArray());
            }
        }
        return map;
    }

    private static void AddIfValid(List<char> list, string row, int a, int b)
    {
        foreach (int i in new[] { a, b }) if (i >= 0 && i < row.Length) list.Add(row[i]);
    }
}
