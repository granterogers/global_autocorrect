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
    SplitsAValidWord,
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

    /// <summary>D37: corpus counts of a word pair and of a word, so a real word is split only when the pair is far more
    /// common ("anew" → "a new"), never otherwise ("inorganic" → "in organic"). Null: real words are never split.</summary>
    public Func<string, string, long>? PairCount { get; init; }
    public Func<string, long>? WordCount { get; init; }

    private bool SplitIsClearlyMeant(TextEdit e) =>
        PairCount is not null && WordCount is not null && e.OriginalWords.Count == 1 && e.NewWords.Count == 2
        && PairCount(e.NewWords[0], e.NewWords[1]) >= 3 * Math.Max(1, WordCount(e.OriginalWords[0]));

    public RuleResult Filter(string original, string cleaned, IReadOnlyList<TextEdit> edits)
    {
        var origTokens = Tokeniser.Tokenise(original);

        // 4.5.d: numbers, URLs and emails must survive untouched, or nothing is applied.
        if (!ProtectedMultiset(origTokens).SequenceEqual(ProtectedMultiset(Tokeniser.Tokenise(cleaned))))
            return Reject(edits, RejectReason.ProtectedTokenChanged);

        // D19: a word "changes" when its spelling does; case, apostrophe, punctuation and spacing fixes don't count.
        int words = origTokens.Count(t => t.IsWord);
        // D36: fixing a word that isn't a word isn't a change of wording, so a badly garbled paragraph can still be fixed.
        int changed = edits.Where(e => e.Kind is EditKind.Substitution or EditKind.Insertion or EditKind.Deletion)
                           .Where(e => !(settings.LenientWithNonWords && e.Kind == EditKind.Substitution && AllNonWords(e.OriginalWords)))
                           .Sum(e => Math.Max(e.OriginalWords.Count, e.NewWords.Count));
        if (changed > settings.MaxChangedWordRatio * Math.Max(1, words))
            return Reject(edits, RejectReason.TooManyChanges);

        var verdicts = edits.Select(e => Judge(original, origTokens, e, edits)).ToArray();
        return new RuleResult(verdicts, RejectReason.None);
    }

    private EditVerdict Judge(string original, IReadOnlyList<Token> origTokens, TextEdit e, IReadOnlyList<TextEdit> all)
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
                // D37: a space never splits a real word: "inorganic" → "in organic" keeps every letter but changes the
                // meaning (user report). Splitting a run-together non-word ("onyour") is still a fix.
                case EditKind.Spacing when e.NewWords.Count > e.OriginalWords.Count && e.OriginalWords.All(IsKnown) && !SplitIsClearlyMeant(e):
                    return RejectReason.SplitsAValidWord;
                case EditKind.Spacing or EditKind.Punctuation or EditKind.Apostrophe:
                    return RejectReason.None;   // 4.5.a
                case EditKind.Capitalisation:
                    return CapitalisationOk(original, e, all) ? RejectReason.None : RejectReason.StyleCapitalisation;
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
    private bool CapitalisationOk(string original, TextEdit e, IReadOnlyList<TextEdit> all)
    {
        string before = original[..e.SpanStart].TrimEnd();
        bool sentenceStart = before.Length == 0 || before[^1] is '.' or '!' or '?' or '\n'
                             || EndsSentenceJustBefore(original, e, all);
        foreach (var (a, b) in e.OriginalWords.Zip(e.NewWords))
        {
            if (a == b) continue;
            bool onlyFirstLetter = a[1..] == b[1..];
            // Sentence start: "the" → "The".
            if (sentenceStart && onlyFirstLetter && char.IsLower(a[0]) && char.IsUpper(b[0])) continue;
            // A stray capital mid-sentence: "On" → "on".
            if (!sentenceStart && onlyFirstLetter && char.IsUpper(a[0]) && char.IsLower(b[0]) && speller.IsValid(b)) continue;
            // What was typed isn't a word as written: proper nouns and "i" ("london" → "London", "i" → "I"). The
            // dictionary lists "i" as a letter, so the pronoun and its contractions are named here.
            if (!speller.IsValid(a) || a is "i" || a.StartsWith("i'", StringComparison.Ordinal)) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// D31: the same reply ends a sentence right before this word ("afternoon something" → "afternoon? Something"), so the
    /// capital is a sentence start even though the user's text has no full stop there yet.
    /// </summary>
    private static bool EndsSentenceJustBefore(string original, TextEdit e, IReadOnlyList<TextEdit> all) =>
        e.Replacement.TakeWhile(c => !char.IsLetter(c)).Any(c => c is '.' or '!' or '?')   // "? S" in one edit
        || all.Any(p => !ReferenceEquals(p, e) && p.End <= e.SpanStart && string.IsNullOrWhiteSpace(original[p.End..e.SpanStart])
                     && p.Replacement.TrimEnd() is { Length: > 0 } r && r[^1] is '.' or '!' or '?');

    /// <summary>
    /// D31: a fix and a dropped function word next to it can be aligned as one edit ("si" → "is the"). Judge the fix on
    /// its own and allow the function word as 4.5.b would.
    /// </summary>
    private RejectReason JudgeSubstitution(TextEdit e)
    {
        if (settings.LenientWithNonWords && AllNonWords(e.OriginalWords) && NonWordFixOk(e)) return RejectReason.None;   // D36
        var reason = JudgeSubstitutionAlone(e);
        int extra = e.NewWords.Count - e.OriginalWords.Count;
        if (reason == RejectReason.None || extra <= 0) return reason;
        // Only when the fix itself is tight: "stationfor" → "station at" must not pass as "stationfor" → "station" (D26).
        if (string.Concat(e.OriginalWords.Select(Similarity.Key)).Length > string.Concat(e.NewWords.Take(e.OriginalWords.Count).Select(Similarity.Key)).Length + 1
            && string.Concat(e.OriginalWords.Select(Similarity.Key)).Length > string.Concat(e.NewWords.Skip(extra).Select(Similarity.Key)).Length + 1)
            return reason;
        // The rest must be a real spelling fix. Same letters in another case ("Spare room" → "The spare room") is an
        // added article plus a lost capital, which the capitalisation rule judges, not a typo (found on the tuning set).
        bool IsFix(IEnumerable<string> words) => !string.Equals(string.Join(' ', e.OriginalWords), string.Join(' ', words), StringComparison.OrdinalIgnoreCase);
        var tail = e.NewWords.Skip(e.OriginalWords.Count).ToArray();
        if (tail.All(_functionWords.Contains) && IsFix(e.NewWords.Take(e.OriginalWords.Count)) && JudgeSubstitutionAlone(e with { NewWords = e.NewWords.Take(e.OriginalWords.Count).ToArray() }) == RejectReason.None)
            return RejectReason.None;
        var head = e.NewWords.Take(extra).ToArray();
        if (head.All(_functionWords.Contains) && IsFix(e.NewWords.Skip(extra)) && JudgeSubstitutionAlone(e with { NewWords = e.NewWords.Skip(extra).ToArray() }) == RejectReason.None)
            return RejectReason.None;
        return reason;
    }

    /// <summary>
    /// D36: not a word as typed. Lower-case words the dictionary doesn't know, and stray single letters other than "a"
    /// and "I". Capitalised unknown words are treated as names and keep the strict rules.
    /// </summary>
    private bool IsNonWord(string w)
    {
        if (w.Length == 0 || !w.Any(char.IsLetter) || w.Any(char.IsDigit)) return false;
        if (w.Length == 1) return w is not ("a" or "A" or "i" or "I");
        return !char.IsUpper(w[0]) && !IsKnown(w);
    }

    private bool AllNonWords(IReadOnlyList<string> words) => words.Count > 0 && words.All(IsNonWord);

    /// <summary>
    /// D36: a fix of a non-word. The result must be real words. Small words added at either end are allowed
    /// ("gorecastsays" → "the forecast says"); what remains must be close in letters: 0.5 apart, or up to
    /// <see cref="EditRuleSettings.MaxNonWordDistance"/> when the first letter is kept ("ovwared" → "over"). A split must
    /// still be nearly exact, so a word swap can't hide in it ("stationfor" → "station at", D26). A stray letter may become
    /// a small word that contains it ("t" → "the").
    /// </summary>
    private bool NonWordFixOk(TextEdit e)
    {
        if (!e.NewWords.All(w => IsKnown(w) || char.IsUpper(w[0]))) return false;
        string a = string.Concat(e.OriginalWords.Select(Similarity.Key));
        if (a.Length == 1)
            return e.NewWords.Count == 1 && _functionWords.Contains(e.NewWords[0]) && Similarity.Key(e.NewWords[0]).Contains(a[0]);
        var nw = e.NewWords;
        for (int head = 0; head < nw.Count; head++)
        {
            if (head > 0 && !_functionWords.Contains(nw[head - 1])) break;
            for (int tail = 0; head + tail < nw.Count; tail++)
            {
                if (tail > 0 && !_functionWords.Contains(nw[nw.Count - tail])) break;
                var rest = nw.Skip(head).Take(nw.Count - head - tail).ToArray();
                string b = string.Concat(rest.Select(Similarity.Key));
                if (b.Length == 0) continue;
                // A dropped small word may only explain added letters, never missing ones: "stationfor" → "station" + "at"
                // would hide "for" becoming "at".
                if (head + tail > 0 && a.Length > b.Length + 1) continue;
                int d = Similarity.DamerauLevenshtein(a, b);
                if (rest.Length != e.OriginalWords.Count && d > 1 && Instant.TypoChannel.TwoSlipLikelihood(a, b) < Instant.TypoChannel.TwoLikelySlips) continue;
                double norm = Similarity.NormDist(a, b);
                if (norm <= settings.MaxNormalisedDistance || (norm <= settings.MaxNonWordDistance && a[0] == b[0])) return true;
            }
        }
        return false;
    }

    private RejectReason JudgeSubstitutionAlone(TextEdit e)
    {
        string a = string.Concat(e.OriginalWords.Select(Similarity.Key));
        string b = string.Concat(e.NewWords.Select(Similarity.Key));
        // D27: one likely keyboard slip (neighbouring key, doubled key, swap) is a typo however short the word is:
        // "q" → "a" is distance 1.0 normalised but the most typical slip there is.
        int distance = Similarity.DamerauLevenshtein(a, b);
        bool oneLikelySlip = settings.AcceptLikelyKeyboardSlips
                             && e.OriginalWords.Count == e.NewWords.Count && distance == 1
                             && Instant.TypoChannel.Likelihood(a, b) >= Instant.TypoChannel.ExtraNeighbour;
        // D30: two likely slips ("by" → "but", "to" → "got", "hteoosition" → "the position") are a typo too, but not two
        // random edits: at most one of them may be an unusual slip.
        bool twoLikelySlips = settings.AcceptLikelyKeyboardSlips && distance == 2
                              && Instant.TypoChannel.TwoSlipLikelihood(a, b) >= Instant.TypoChannel.TwoLikelySlips;
        bool likelySlips = oneLikelySlip || twoLikelySlips;
        if (!likelySlips && Similarity.NormDist(a, b) > settings.MaxNormalisedDistance) return RejectReason.TooDissimilar;
        // When words are split or merged, the letters must be almost the same; otherwise a long run-together word
        // dilutes the distance and hides a word swap ("stationfor" → "station at").
        if (e.OriginalWords.Count != e.NewWords.Count && distance > 1 && !twoLikelySlips) return RejectReason.TooDissimilar;

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

    // D31: a possessive or "is" contraction of a known word ("heating's") is known too; the dictionary lists few of them.
    private bool IsKnown(string w) =>
        speller.IsValid(w) || isPersonalWord(w) || (w.Length > 2 && w.EndsWith("'s", StringComparison.OrdinalIgnoreCase) && speller.IsValid(w[..^2]));

    private static IEnumerable<string> ProtectedMultiset(IEnumerable<Token> tokens) =>
        tokens.Where(t => t.IsWord && (t.Flags & (TokenFlags.Number | TokenFlags.Url | TokenFlags.Email)) != 0)
              .Select(t => t.Text).Order(StringComparer.Ordinal);

    private static RuleResult Reject(IReadOnlyList<TextEdit> edits, RejectReason why) =>
        new(edits.Select(e => new EditVerdict(e, false, why)).ToArray(), why);
}
