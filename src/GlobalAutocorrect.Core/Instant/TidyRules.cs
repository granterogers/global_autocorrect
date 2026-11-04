using System.Text.RegularExpressions;

namespace GlobalAutocorrect.Core.Instant;

/// <summary>A spacing or punctuation fix in typed-buffer coordinates: replace typed[Start..End] (Original).</summary>
public sealed record TidyFix(int Start, int End, string Original, string Replacement, string Kind);

/// <summary>
/// D55: spacing and punctuation slips fixed by the instant layer (4.3) the moment the next space or Enter is typed,
/// with no AI: a space before punctuation ("steps ."), two spaces between words, a missing space after a full stop
/// ("end.Next"), a lower-case "i", and a lower-case word starting a sentence. Each rule only looks at the chunk just
/// finished and the characters right before it, and each leaves alone what is often deliberate: names with dots
/// (.NET, index.html, e.g.), emoticons, ellipses, numbers, indentation, two spaces after a full stop, and code.
/// </summary>
public sealed class TidyRules(ISpellChecker speller)
{
    private static readonly Regex MissingSpaceAfterStop = new(@"^(\p{Ll}{2,})([.!?])(\p{Lu}\p{Ll}+)([.,;:!?]?)$", RegexOptions.CultureInvariant);
    private static readonly Regex MissingSpaceAfterComma = new(@"^(\p{Ll}{4,}),(\p{Ll}{4,})([.,;:!?]?)$", RegexOptions.CultureInvariant);
    private static readonly Regex LowerI = new(@"^i(['’](m|ve|ll|d))?[.,;:!?]?$", RegexOptions.CultureInvariant);
    private static readonly Regex LowerWord = new(@"^\p{Ll}[\p{Ll}'’]*[.,;:!?]?$", RegexOptions.CultureInvariant);

    /// <summary>Words that end with a full stop without ending the sentence.</summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "st", "no", "vs", "etc", "approx", "fig", "cf", "al", "inc", "ltd", "co", "dept", "est",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec", "mon", "tue", "wed", "thu", "fri",
    };

    /// <param name="typed">The typed text, ending with the whitespace character just typed.</param>
    public TidyFix? Find(string typed)
    {
        if (typed.Length < 2 || !char.IsWhiteSpace(typed[^1])) return null;
        int end = typed.Length - 1;
        int start = end;
        while (start > 0 && !char.IsWhiteSpace(typed[start - 1])) start--;
        if (start == end) return null;
        string chunk = typed[start..end];

        return SpaceBeforePunctuation(typed, start, chunk)
            ?? MissingSpaceAfter(start, chunk)
            ?? GluedNumber(start, chunk)   // D82
            ?? DoubleSpace(typed, start, chunk)
            ?? CapitalI(typed, start, chunk)
            ?? TwoCapitals(typed, start, chunk)
            ?? SentenceStart(typed, start, chunk);
    }

    private static readonly System.Text.RegularExpressions.Regex TwoInitialCapitals = new(@"^(\p{Lu})(\p{Lu})(\p{Ll}+)[.,;:!?]?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex TwoLetterCaps = new(@"^(\p{Lu})(\p{Lu})[.,;:!?]?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>D78: two-letter small words that Shift held a moment too long turns into capitals.</summary>
    private static readonly HashSet<string> SmallWords = new(StringComparer.Ordinal)
    {
        "IF", "IN", "IS", "ON", "TO", "WE", "MY", "ME", "SO", "AN", "AT", "AS", "BE", "BY", "DO", "GO", "HE", "OR", "OF",
    };

    // D78: Shift held a moment too long: "THe" → "The", and at the start of a sentence or line "IF" → "If" (the user's
    // Intercom reply). Never when the text before is in capitals too (shouting), never IT, US, UK, OK and the like.
    private TidyFix? TwoCapitals(string typed, int start, string chunk)
    {
        var m = TwoInitialCapitals.Match(chunk);
        if (m.Success)
        {
            string word = m.Groups[1].Value + m.Groups[2].Value.ToLowerInvariant() + m.Groups[3].Value;
            if (!speller.IsValid(word) || PreviousWordIsCapitals(typed, start)) return null;
            return new TidyFix(start + 1, start + 2, m.Groups[2].Value, m.Groups[2].Value.ToLowerInvariant(), "two-capitals");
        }
        m = TwoLetterCaps.Match(chunk);
        if (!m.Success || !SmallWords.Contains(m.Groups[1].Value + m.Groups[2].Value) || PreviousWordIsCapitals(typed, start)) return null;
        int i = start - 1;
        while (i >= 0 && typed[i] == ' ') i--;
        if (i < 0 || typed[i] is not ('.' or '!' or '?' or '\n')) return null;   // only where a capital is due anyway
        return new TidyFix(start + 1, start + 2, m.Groups[2].Value, m.Groups[2].Value.ToLowerInvariant(), "two-capitals");
    }

    /// <summary>The word before (if any, within the last line or two) is all capitals: the user is writing in capitals.</summary>
    private static bool PreviousWordIsCapitals(string typed, int start)
    {
        int e = start - 1;
        while (e >= 0 && !char.IsLetter(typed[e])) e--;
        if (e < 0) return false;
        int s = e;
        while (s > 0 && char.IsLetter(typed[s - 1])) s--;
        string w = typed[s..(e + 1)];
        return w.Length >= 2 && w.All(char.IsUpper);
    }

    // "steps ." → "steps.": a run of sentence punctuation typed after one space that follows a letter. Decided at the
    // space after it, so ".NET" or ":)" have shown themselves first.
    private static TidyFix? SpaceBeforePunctuation(string typed, int start, string chunk)
    {
        if (chunk.Length > 3 || !chunk.All(c => c is '.' or ',' or ';' or ':' or '!' or '?')) return null;
        if (chunk.Contains('.') && chunk != ".") return null;   // ellipses
        if (start < 2 || typed[start - 1] != ' ' || !char.IsLetter(typed[start - 2])) return null;
        return new TidyFix(start - 1, start + chunk.Length, " " + chunk, chunk, "space-before-punctuation");
    }

    // "end.Next" → "end. Next", "apples,pears" → "apples, pears": both sides real words.
    private TidyFix? MissingSpaceAfter(int start, string chunk)
    {
        var m = MissingSpaceAfterStop.Match(chunk);
        if (!m.Success) m = MissingSpaceAfterComma.Match(chunk);
        if (!m.Success || !speller.IsValid(m.Groups[1].Value) || !speller.IsValid(m.Groups[^2].Value.ToLowerInvariant())) return null;
        int at = start + m.Groups[1].Length;
        string p = chunk[m.Groups[1].Length].ToString();
        return new TidyFix(at, at + 1, p, p + " ", "missing-space");
    }

    private static readonly Regex WordThenNumber = new(@"^(\p{L}{2,})(\d{1,4})(st|nd|rd|th|am|pm)?([.,;:!?]?)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex OrdinalThenWord = new(@"^(\d{1,2})(st|nd|rd|th)(\p{L}{3,})([.,;:!?]?)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>D82: words that are followed by a plain number or a time ("at 1500", "November 5", "by 3pm").</summary>
    private static readonly HashSet<string> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "at", "by", "on", "from", "until", "till", "before", "after", "around", "about", "today", "tomorrow", "tonight", "yesterday",
        "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "page", "step", "room", "floor",
    };

    /// <summary>"1st", "2nd", "3rd", "4th", "11th", "21st": the suffix the number takes.</summary>
    private static bool RightOrdinal(string digits, string suffix)
    {
        int n = int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        string want = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return suffix.Equals(want, StringComparison.OrdinalIgnoreCase);
    }

    // D82 (user report, Intercom: "tomorrow5th November"): a word and a number typed without the space between them.
    // "tomorrow5th" → "tomorrow 5th", "at1500" → "at 1500", "5thNovember" → "5th November". Only a real word with a
    // correct ordinal, or a word that comes before numbers and times (NumberWords) with any number: never codes and
    // names like "Windows11", "mp3", "covid19" or "office365". The number itself is never changed (4.5.d).
    private TidyFix? GluedNumber(int start, string chunk)
    {
        var m = WordThenNumber.Match(chunk);
        if (m.Success)
        {
            string word = m.Groups[1].Value, digits = m.Groups[2].Value, suffix = m.Groups[3].Value;
            bool ordinal = suffix.Length > 0 && suffix.ToLowerInvariant() is "st" or "nd" or "rd" or "th";
            if (ordinal && !RightOrdinal(digits, suffix)) return null;   // "5rd": not sure what was meant
            bool known = speller.IsValid(word) || speller.IsValid(word.ToLowerInvariant());
            if (!NumberWords.Contains(word) && !(ordinal && word.Length >= 3 && known)) return null;
            int at = start + word.Length;
            return new TidyFix(at, at + 1, digits[..1], " " + digits[..1], "glued-number");
        }
        m = OrdinalThenWord.Match(chunk);
        if (!m.Success || !RightOrdinal(m.Groups[1].Value, m.Groups[2].Value)) return null;
        string after = m.Groups[3].Value;
        if (!speller.IsValid(after) && !speller.IsValid(after.ToLowerInvariant())) return null;
        int end = start + m.Groups[1].Length + m.Groups[2].Length;
        return new TidyFix(end - 1, end, chunk[(end - 1 - start)..(end - start)], chunk[(end - 1 - start)..(end - start)] + " ", "glued-number");
    }

    // "with  the" → "with the": exactly two spaces after a letter or a comma-like mark (not after a full stop: two spaces
    // there is a typing style).
    private static TidyFix? DoubleSpace(string typed, int start, string chunk)
    {
        if (!char.IsLetter(chunk[0])) return null;
        if (start < 3 || typed[start - 1] != ' ' || typed[start - 2] != ' ') return null;
        char before = typed[start - 3];
        if (!(char.IsLetter(before) || before is ',' or ';' or ':')) return null;
        return new TidyFix(start - 2, start, "  ", " ", "double-space");
    }

    // "i" → "I", "i'm" → "I'm", unless the line looks like code ("for i in", "x = i").
    private static TidyFix? CapitalI(string typed, int start, string chunk)
    {
        if (!LowerI.IsMatch(chunk) || LooksLikeCode(typed, start)) return null;
        return new TidyFix(start, start + 1, "i", "I", "capital-i");
    }

    // ". then" → ". Then": only after . ! or ? on the same line, never after an abbreviation, initials or an ellipsis.
    private TidyFix? SentenceStart(string typed, int start, string chunk)
    {
        if (!LowerWord.IsMatch(chunk)) return null;
        string word = chunk.TrimEnd('.', ',', ';', ':', '!', '?');
        if (!speller.IsValid(word)) return null;
        int i = start - 1, spaces = 0;
        while (i >= 0 && typed[i] == ' ') { i--; spaces++; }
        if (i < 1 || spaces is 0 or > 2 || typed[i] is not ('.' or '!' or '?')) return null;
        if (typed[i] == '.')
        {
            if (typed[i - 1] == '.') return null;   // "wait... what"
            int s = i;
            while (s > 0 && !char.IsWhiteSpace(typed[s - 1])) s--;
            string previous = typed[s..i];
            if (previous.Length <= 1 || previous.Contains('.') || previous.Any(char.IsDigit) || Abbreviations.Contains(previous)) return null;
        }
        string first = chunk[..1];
        return new TidyFix(start, start + 1, first, first.ToUpperInvariant(), "sentence-start");
    }

    private static bool LooksLikeCode(string typed, int start)
    {
        int lineStart = typed.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        string line = typed[lineStart..];
        if (line.IndexOfAny(['=', '{', '}', '[', ']', ';', '<', '>']) >= 0) return true;
        // "for i in", "let i", "int i": a loop variable, not the pronoun.
        if (start <= lineStart) return false;   // first on its line: no word before it
        int p = start - 1;
        while (p > lineStart && typed[p - 1] == ' ') p--;
        int ps = p;
        while (ps > lineStart && !char.IsWhiteSpace(typed[ps - 1])) ps--;
        return typed[ps..p] is "for" or "let" or "var" or "int" or "foreach";
    }
}
