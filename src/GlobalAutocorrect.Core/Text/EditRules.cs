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
                case EditKind.Spacing or EditKind.Punctuation or EditKind.Apostrophe or EditKind.Capitalisation:
                    return RejectReason.None;   // 4.5.a
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

    private RejectReason JudgeSubstitution(TextEdit e)
    {
        string a = string.Concat(e.OriginalWords.Select(Similarity.Key));
        string b = string.Concat(e.NewWords.Select(Similarity.Key));
        if (Similarity.NormDist(a, b) > settings.MaxNormalisedDistance) return RejectReason.TooDissimilar;

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
