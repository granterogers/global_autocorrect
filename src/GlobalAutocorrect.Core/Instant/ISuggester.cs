namespace GlobalAutocorrect.Core.Instant;

public readonly record struct SpellSuggestion(string Word, int Distance, long Frequency);

/// <summary>SymSpell lookup (1.5): closest dictionary words, ordered by (distance, -frequency).</summary>
public interface ISuggester
{
    IReadOnlyList<SpellSuggestion> Lookup(string word, int max = 3);
}
