using SymSpellEngine = SymSpell;
using SymSpellVerbosity = SymSpell.Verbosity;

namespace GlobalAutocorrect.Core.Instant;

/// <summary>
/// SymSpell suggester over the en_GB frequency list (1.5, 3.4). Edit distance 2, prefix length 7.
/// Thread-safe: the dictionary is only written while loading in the constructor; <see cref="Lookup"/> is read-only afterwards,
/// so concurrent lookups are safe.
/// </summary>
public sealed class SymSpellSuggester : ISuggester
{
    private const int MaxEditDistance = 2;
    private const int PrefixLength = 7;

    private readonly SymSpellEngine _symSpell;

    /// <summary>Loads a "word count" (space separated) frequency file.</summary>
    public SymSpellSuggester(string freqPath)
    {
        _symSpell = new SymSpellEngine(80_000, MaxEditDistance, PrefixLength);
        if (!_symSpell.LoadDictionary(freqPath, 0, 1, [' ']))
            throw new FileNotFoundException("Frequency dictionary could not be loaded.");
    }

    /// <summary>Loads <c>freq_en_GB.txt</c> from <paramref name="assetsDir"/>.</summary>
    public static SymSpellSuggester FromAssets(string assetsDir) => new(Path.Combine(assetsDir, "freq_en_GB.txt"));

    public IReadOnlyList<SpellSuggestion> Lookup(string word, int max = 3)
    {
        if (string.IsNullOrEmpty(word) || max <= 0) return [];
        return _symSpell.Lookup(word.ToLowerInvariant(), SymSpellVerbosity.All, MaxEditDistance)
            .OrderBy(s => s.distance)
            .ThenByDescending(s => s.count)
            .Take(max)
            .Select(s => new SpellSuggestion(s.term, s.distance, s.count))
            .ToList();
    }
}
