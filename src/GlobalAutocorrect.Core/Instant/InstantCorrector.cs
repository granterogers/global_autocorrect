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

    /// <returns>The replacement word, or null to leave the word alone.</returns>
    public string? TryCorrect(string word, bool isSentenceStart)
    {
        // 4.3.c, in order: valid, personal, capitalised mid-sentence, digits, URL/email/code-like.
        if (word.Length < 2) return null;
        if (speller.IsValid(word)) return null;
        if (isPersonalWord(word)) return null;
        if (char.IsUpper(word[0]) && !isSentenceStart) return null;
        if (word.Any(char.IsDigit)) return null;
        if (Tokeniser.Tokenise(word) is not [{ IsWord: true, IsProtected: false } t] || t.Length != word.Length) return null;

        string lower = word.ToLowerInvariant();
        var candidates = suggester.Lookup(lower, 3);
        if (candidates.Count == 0) return null;
        var top = candidates[0];
        if (top.Word == lower) return null;
        // The candidate must itself be valid en_GB-ise, so the instant layer can never Americanise (3.4).
        if (!speller.IsValid(top.Word)) return null;

        bool certain = top.Distance == 1
            ? candidates.Count < 2 || candidates[1].Distance > 1 || top.Frequency > candidates[1].Frequency
            : top.Distance == 2 && (candidates.Count < 2 || top.Frequency >= Distance2FrequencyRatio * candidates[1].Frequency);
        // A distance-1 tie between equally likely words is not "obvious": leave it for the AI (4.3.d).
        if (!certain) return null;

        return MatchCase(word, top.Word);
    }

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
