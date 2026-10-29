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

    /// <summary>Settings changed (by us, a shortcut or the tray). Can fire on any thread.</summary>
    event Action? Changed;
}
