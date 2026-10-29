using GlobalAutocorrect.Core.Instant;

namespace GlobalAutocorrect.Core.Text;

public enum RejectReason
{
    None,
    ProtectedTokenChanged,
    TooManyChanges,
    TouchesProtectedToken,
    PersonalDictionary,
    CrossesLine,
    TooDissimilar,
    NotAValidWord,
    ReplacesValidWithInvalid,
    NotAFunctionWord,
    StyleCapitalisation,
}

public sealed record EditVerdict(TextEdit Edit, bool Accepted, RejectReason Reason);

public sealed record RuleResult(IReadOnlyList<EditVerdict> Verdicts, RejectReason WholeRejection)
{
    public IReadOnlyList<TextEdit> Accepted => WholeRejection == RejectReason.None
        ? Verdicts.Where(v => v.Accepted).Select(v => v.Edit).ToArray()
        : [];
}

/// <summary>
/// The "no rewrite" rule (4.5): keeps edits that fix mistakes and drops anything that changes the user's wording.
/// </summary>
public sealed class EditRules(EditRuleSettings settings, ISpellChecker speller, Func<string, bool> isPersonalWord)
{
    private readonly HashSet<string> _functionWords = new(settings.FunctionWords, StringComparer.OrdinalIgnoreCase);

    public EditRuleSettings Settings => settings;

    public RuleResult Filter(string original, string cleaned, IReadOnlyList<TextEdit> edits)
    {
        var origTokens = Tokeniser.Tokenise(original);

        // 4.5.d: numbers, URLs and emails must survive untouched, or nothing is applied.
        if (!ProtectedMultiset(origTokens).SequenceEqual(ProtectedMultiset(Tokeniser.Tokenise(cleaned))))
            return Reject(edits, RejectReason.ProtectedTokenChanged);

        // D19: a word "changes" when its spelling does; case, apostrophe, punctuation and spacing fixes don't count.
        int words = origTokens.Count(t => t.IsWord);
        int changed = edits.Where(e => e.Kind is EditKind.Substitution or EditKind.Insertion or EditKind.Deletion)
                           .Sum(e => Math.Max(e.OriginalWords.Count, e.NewWords.Count));
        if (changed > settings.MaxChangedWordRatio * Math.Max(1, words))
            return Reject(edits, RejectReason.TooManyChanges);

        var verdicts = edits.Select(e => Judge(original, origTokens, e)).ToArray();
        return new RuleResult(verdicts, RejectReason.None);
    }

    private EditVerdict Judge(string original, IReadOnlyList<Token> origTokens, TextEdit e)
    {
        RejectReason Why()
        {
            if (original.AsSpan(e.Start, e.End - e.Start).ContainsAny('\r', '\n') || e.Replacement.ContainsAny('\r', '\n'))
                return RejectReason.CrossesLine;   // D8
            foreach (var t in origTokens)
            {
                if (!t.IsWord || t.End <= e.SpanStart || t.Start >= e.SpanEnd) continue;
                // Run-together words like "nextFriday" look code-like; re-spacing them changes no characters, so it's allowed.
                // Numbers, URLs and emails are never touched, not even their spacing (4.5.d).
                bool hardProtected = (t.Flags & (TokenFlags.Number | TokenFlags.Url | TokenFlags.Email)) != 0;
                if (hardProtected || (t.IsProtected && e.Kind != EditKind.Spacing)) return RejectReason.TouchesProtectedToken;
                if (isPersonalWord(t.Text)) return RejectReason.PersonalDictionary;
            }
            switch (e.Kind)
            {
                case EditKind.Spacing or EditKind.Punctuation or EditKind.Apostrophe:
                    return RejectReason.None;   // 4.5.a
                case EditKind.Capitalisation:
                    return CapitalisationOk(original, e) ? RejectReason.None : RejectReason.StyleCapitalisation;
                case EditKind.Insertion or EditKind.Deletion:
                    var w = e.Kind == EditKind.Insertion ? e.NewWords : e.OriginalWords;
                    return w.All(_functionWords.Contains) ? RejectReason.None : RejectReason.NotAFunctionWord;   // 4.5.b
                case EditKind.Substitution:
                    return JudgeSubstitution(e);
                default:
                    return RejectReason.TooDissimilar;   // 4.5.c
            }
        }

        var reason = Why();
        return new EditVerdict(e, reason == RejectReason.None, reason);
    }

    /// <summary>
    /// A capital is a fix at a sentence start, or where the lower-case form isn't a word on its own ("london", "monday",
    /// "i"). Capitalising an ordinary word mid-sentence ("senior analyst" → "Senior Analyst") is style, not a mistake (4.4.d).
    /// </summary>
    private bool CapitalisationOk(string original, TextEdit e)
    {
        string before = original[..e.SpanStart].TrimEnd();
        bool sentenceStart = before.Length == 0 || before[^1] is '.' or '!' or '?' or '\n';
        foreach (var (a, b) in e.OriginalWords.Zip(e.NewWords))
        {
            if (a == b) continue;
            bool onlyFirstLetter = a[1..] == b[1..];
            // Sentence start: "the" → "The".
            if (sentenceStart && onlyFirstLetter && char.IsLower(a[0]) && char.IsUpper(b[0])) continue;
            // A stray capital mid-sentence: "On" → "on".
            if (!sentenceStart && onlyFirstLetter && char.IsUpper(a[0]) && char.IsLower(b[0]) && speller.IsValid(b)) continue;
            // What was typed isn't a word as written: proper nouns and "i" ("london" → "London", "i" → "I").
            if (!speller.IsValid(a)) continue;
            return false;
        }
        return true;
    }

    private RejectReason JudgeSubstitution(TextEdit e)
    {
        string a = string.Concat(e.OriginalWords.Select(Similarity.Key));
        string b = string.Concat(e.NewWords.Select(Similarity.Key));
        if (Similarity.NormDist(a, b) > settings.MaxNormalisedDistance) return RejectReason.TooDissimilar;
        // When words are split or merged, the letters must be almost the same; otherwise a long run-together word
        // dilutes the distance and hides a word swap ("stationfor" → "station at").
        if (e.OriginalWords.Count != e.NewWords.Count && Similarity.DamerauLevenshtein(a, b) > 1) return RejectReason.TooDissimilar;

        // D16: never trade a correctly spelt word for one that isn't valid en_GB-ise (blocks Americanisation, 3.4).
        bool origValid = e.OriginalWords.All(IsKnown);
        foreach (var w in e.NewWords)
        {
            if (IsKnown(w)) continue;
            if (origValid) return RejectReason.ReplacesValidWithInvalid;
            if (!char.IsUpper(w[0])) return RejectReason.NotAValidWord;   // unknown capitalised words may be names
        }
        return RejectReason.None;
    }

    private bool IsKnown(string w) => speller.IsValid(w) || isPersonalWord(w);

    private static IEnumerable<string> ProtectedMultiset(IEnumerable<Token> tokens) =>
        tokens.Where(t => t.IsWord && (t.Flags & (TokenFlags.Number | TokenFlags.Url | TokenFlags.Email)) != 0)
              .Select(t => t.Text).Order(StringComparer.Ordinal);

    private static RuleResult Reject(IReadOnlyList<TextEdit> edits, RejectReason why) =>
        new(edits.Select(e => new EditVerdict(e, false, why)).ToArray(), why);
}
