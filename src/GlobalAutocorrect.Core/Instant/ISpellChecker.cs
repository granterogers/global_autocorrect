namespace GlobalAutocorrect.Core.Instant;

/// <summary>Hunspell en_GB-ise validity (1.5, 3.4).</summary>
public interface ISpellChecker
{
    bool IsValid(string word);
}
