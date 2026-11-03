namespace GlobalAutocorrect.Core.Targets;

/// <summary>
/// D76 (8.6): how the field's text differs from the typed record, as kinds of character only, never the characters, so
/// it can be logged in normal use. In Intercom the field disagreed with the record 83 times in an afternoon, which no
/// test editor does; this says what Intercom does differently.
/// </summary>
public static class MismatchKind
{
    public static string Describe(string typed, string field)
    {
        string t = TextNorm.Comparable(typed), f = TextNorm.Comparable(field);
        int same = 0;
        while (same < t.Length && same < f.Length && t[^(same + 1)] == f[^(same + 1)]) same++;
        string Kind(string s) => same < s.Length ? Of(s[^(same + 1)]) : "start";
        return $"typed={t.Length} field={f.Length} sameFromCaret={same} typedHas={Kind(t)} fieldHas={Kind(f)}"
               + $" fieldObjects={f.Count(c => c == '￼')} fieldNbsp={f.Count(c => c == ' ')} fieldZeroWidth={f.Count(IsZeroWidth)}";
    }

    public static string Of(char c) => c switch
    {
        '￼' => "object",
        ' ' => "nbsp",
        '\n' => "newline",
        '\r' => "cr",
        ' ' => "space",
        '\t' => "tab",
        _ when IsZeroWidth(c) => "zero-width",
        _ when char.IsUpper(c) => "capital",
        _ when char.IsLetter(c) => "letter",
        _ when char.IsDigit(c) => "digit",
        _ when char.IsPunctuation(c) => "punctuation",
        _ when char.IsSymbol(c) => "symbol",
        _ => "other",
    };

    private static bool IsZeroWidth(char c) => c is '​' or '‌' or '‍' or '⁠' or '﻿';
}
