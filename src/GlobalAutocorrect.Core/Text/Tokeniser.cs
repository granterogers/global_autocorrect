using System.Globalization;
using System.Text.RegularExpressions;

namespace GlobalAutocorrect.Core.Text;

/// <summary>
/// Splits text into words, punctuation, spaces and newlines, keeping offsets.
/// Words keep internal apostrophes ("don't"), hyphens ("well-known") and number punctuation ("10:30", "2.5", "1,000").
/// </summary>
public static partial class Tokeniser
{
    [GeneratedRegex(@"\G(?:(?:https?://|www\.)\S+|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})", RegexOptions.CultureInvariant)]
    private static partial Regex UrlOrEmail();

    public static IReadOnlyList<Token> Tokenise(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            int s = i;

            if (c is '\r' or '\n')
            {
                i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                tokens.Add(new Token(TokenKind.Newline, s, i, text[s..i], TokenFlags.None));
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]) && text[i] is not ('\r' or '\n')) i++;
                tokens.Add(new Token(TokenKind.Space, s, i, text[s..i], TokenFlags.None));
                continue;
            }

            if (IsChunkStart(text, i))
            {
                var m = UrlOrEmail().Match(text, i);
                if (m.Success)
                {
                    int end = i + m.Length;
                    while (end > i + 1 && IsTrailingPunct(text[end - 1])) end--;
                    string w = text[i..end];
                    var flag = w.Contains('@') && !w.Contains("://", StringComparison.Ordinal) ? TokenFlags.Email : TokenFlags.Url;
                    tokens.Add(new Token(TokenKind.Word, i, end, w, flag));
                    i = end;
                    continue;
                }
            }

            if (IsWordStart(text, i))
            {
                i++;
                while (i < text.Length && IsWordContinue(text, i)) i++;
                string w = text[s..i];
                tokens.Add(new Token(TokenKind.Word, s, i, w, Classify(w)));
                continue;
            }

            // Keep surrogate pairs (emoji) together as one punctuation token.
            i += char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            tokens.Add(new Token(TokenKind.Punct, s, i, text[s..i], TokenFlags.None));
        }
        return tokens;
    }

    /// <summary>Number, CodeLike flags for a plain word (URLs and emails are detected before this).</summary>
    public static TokenFlags Classify(string w)
    {
        var flags = TokenFlags.None;
        if (w.Any(char.IsDigit)) flags |= TokenFlags.Number;
        if (w.Contains('_') || w[0] is '#' or '@' || HasInnerCapital(w)) flags |= TokenFlags.CodeLike;
        return flags;
    }

    private static bool HasInnerCapital(string w)
    {
        for (int i = 1; i < w.Length; i++)
        {
            if (!char.IsUpper(w[i]) || !char.IsLower(w[i - 1])) continue;
            // D48: a capital I closing a run-together word ("thinkI", "sI") is a missed space before "I", not code.
            if (w[i] == 'I' && (i == w.Length - 1 || !char.IsLetter(w[i + 1]))) continue;
            return true;
        }
        return false;
    }

    private static bool IsChunkStart(string t, int i) => i == 0 || char.IsWhiteSpace(t[i - 1]) || t[i - 1] is '(' or '<' or '"' or '[';

    private static bool IsTrailingPunct(char c) => c is '.' or ',' or '!' or '?' or ';' or ':' or ')' or ']' or '}' or '\'' or '"' or '>';

    private static bool IsLetterOrDigit(char c) => char.IsLetterOrDigit(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark;

    private static bool IsWordStart(string t, int i)
    {
        char c = t[i];
        if (IsLetterOrDigit(c) || c == '_') return true;
        bool nextAlnum = i + 1 < t.Length && char.IsLetterOrDigit(t[i + 1]);
        if (c is '£' or '$' or '€') return i + 1 < t.Length && char.IsDigit(t[i + 1]);
        if (c is '#' or '@') return nextAlnum && IsChunkStart(t, i);
        return false;
    }

    private static bool IsWordContinue(string t, int i)
    {
        char c = t[i];
        if (IsLetterOrDigit(c) || c == '_') return true;
        char prev = t[i - 1];
        bool hasNext = i + 1 < t.Length;
        char next = hasNext ? t[i + 1] : '\0';
        return c switch
        {
            '\'' or '’' => char.IsLetter(prev) && hasNext && char.IsLetter(next),
            '-' => char.IsLetterOrDigit(prev) && hasNext && char.IsLetterOrDigit(next),
            '.' or ',' or ':' or '/' => char.IsDigit(prev) && hasNext && char.IsDigit(next),
            '%' => char.IsDigit(prev),
            _ => false,
        };
    }
}
