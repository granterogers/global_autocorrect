using GlobalAutocorrect.Core.Dictionary;
using GlobalAutocorrect.Core.Settings;

namespace GlobalAutocorrect.App.Services;

/// <summary>
/// What the settings pages need from the app: the current settings, a way to save a change at once (9.3), and the
/// personal dictionary (8.5). View models depend on this, never on <see cref="AppHost"/>, so tests need no running app.
/// </summary>
public interface ISettingsService
{
    AppSettings Settings { get; }
    PersonalDictionary Dictionary { get; }

    /// <summary>Saves and applies new settings straight away. There is no Save button.</summary>
    void Update(AppSettings settings);

    /// <summary>D142: the phrases learned from the user's repeats (null where there are none, e.g. tests).</summary>
    GlobalAutocorrect.Core.Prediction.PhraseMemory? Phrases => null;

    /// <summary>D142: saves the phrases now (after one was removed in Settings).</summary>
    void SavePhrases() { }

    /// <summary>Settings changed (by us, a shortcut or the tray). Can fire on any thread.</summary>
    event Action? Changed;
}
