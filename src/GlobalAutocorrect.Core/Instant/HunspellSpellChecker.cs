using WeCantSpell.Hunspell;

namespace GlobalAutocorrect.Core.Instant;

/// <summary>Hunspell en_GB-ise (1.5, 3.4). Loaded once; checks are thread-safe.</summary>
public sealed class HunspellSpellChecker : ISpellChecker
{
    private readonly WordList _words;

    public HunspellSpellChecker(string dicPath, string affPath)
    {
        _words = WordList.CreateFromFiles(dicPath, affPath);
    }

    /// <summary>Loads en_GB-ise.dic/.aff from <paramref name="assetsDir"/>.</summary>
    public static HunspellSpellChecker FromAssets(string assetsDir) =>
        new(Path.Combine(assetsDir, "en_GB-ise.dic"), Path.Combine(assetsDir, "en_GB-ise.aff"));

    public bool IsValid(string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        // Typed apostrophes may be curly; the dictionary uses straight ones.
        return _words.Check(word.Replace('’', '\''));
    }
}
