using GlobalAutocorrect.Core.Text;

namespace GlobalAutocorrect.Core.Instant;

/// <summary>
/// The instant layer (4.3): fixes an obviously misspelt word as soon as it is finished, with no AI (4.3.b).
/// Anything less than certain is left for the AI layer (4.3.d).
/// </summary>
public sealed class InstantCorrector(ISpellChecker speller, ISuggester suggester, Func<string, bool> isPersonalWord)
{
    /// <summary>4.3.c: distance 2 is only taken when the top candidate is this many times more frequent than the next.</summary>
    public double Distance2FrequencyRatio { get; init; } = 10;

    /// <summary>
    /// D23: at distance 1, candidates are ranked by frequency × how likely that slip is on a UK keyboard, and the winner
    /// must beat the next by this factor. "bookk" → "book" (doubled key) is clear; "ofr" → "of" or "for" is not, so it
    /// goes to the AI, which can use the context (4.3.d).
    /// </summary>
    public double Distance1ScoreRatio { get; init; } = 4;

    /// <param name="previousWord">The word just before, if only a space separates them: "s andwich" is a misplaced space.</param>
    /// <returns>The replacement word, or null to leave the word alone.</returns>
    public string? TryCorrect(string word, bool isSentenceStart, string? previousWord = null)
    {
        if (previousWord is not null && speller.IsValid(previousWord + word)) return null;   // D27: leave the space to the AI
        // 4.3.c, in order: valid, personal, capitalised mid-sentence, digits, URL/email/code-like.
        if (word.Length < 2) return null;
        if (speller.IsValid(word)) return null;
        if (isPersonalWord(word)) return null;
        if (char.IsUpper(word[0]) && !isSentenceStart) return null;
        if (word.Any(char.IsDigit)) return null;
        if (Tokeniser.Tokenise(word) is not [{ IsWord: true, IsProtected: false } t] || t.Length != word.Length) return null;

        string lower = word.ToLowerInvariant();
        // A run-together pair ("ifyou", "thebreakdown") is a missing space, which the AI fixes (4.4.c). Don't
        // "correct" it by deleting letters.
        if (SplitsIntoTwoWords(lower)) return null;

        var candidates = suggester.Lookup(lower, 10);
        if (candidates.Count == 0 || candidates[0].Word == lower) return null;

        string? pick = candidates[0].Distance switch
        {
            1 => PickDistance1(lower, candidates.Where(c => c.Distance == 1).ToArray()),
            2 => candidates.Count < 2 || candidates[1].Distance > 2 || candidates[0].Frequency >= Distance2FrequencyRatio * candidates[1].Frequency
                ? candidates[0].Word : null,   // 4.3.c exactly
            _ => null,
        };
        // The candidate must itself be valid en_GB-ise, so the instant layer can never Americanise (3.4).
        if (pick is null || !speller.IsValid(pick)) return null;   // 4.3.d
        return MatchCase(word, pick);
    }

    private string? PickDistance1(string typed, SpellSuggestion[] d1)
    {
        var scored = d1.Select(c => (c.Word, Score: c.Frequency * TypoChannel.Likelihood(typed, c.Word)))
                       .OrderByDescending(x => x.Score).ToArray();
        if (scored.Length == 1 || scored[0].Score >= Distance1ScoreRatio * scored[1].Score) return scored[0].Word;
        return null;
    }

    private bool SplitsIntoTwoWords(string w)
    {
        for (int i = 1; i < w.Length; i++)
        {
            string left = w[..i], right = w[i..];
            if (IsWordPart(left) && IsWordPart(right)) return true;
        }
        return false;
    }

    private bool IsWordPart(string s) => (s.Length >= 2 || s is "a" or "i") && speller.IsValid(s);

    /// <summary>Keeps the user's capitalisation pattern: "Teh" → "The", "TEH" → "THE".</summary>
    public static string MatchCase(string original, string replacement)
    {
        if (original.Length > 1 && original.All(c => !char.IsLetter(c) || char.IsUpper(c)))
            return replacement.ToUpperInvariant();
        if (char.IsUpper(original[0]))
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        return replacement;
    }
}
