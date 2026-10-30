using GlobalAutocorrect.Core.Updates;

namespace GlobalAutocorrect.App.Services;

/// <summary>D58: what the About page needs from the update check. Tests use a fake.</summary>
public interface IUpdateService
{
    /// <summary>A newer release found by the last check, or null.</summary>
    AvailableUpdate? Available { get; }

    /// <summary>An update is being downloaded and checked.</summary>
    bool Installing { get; }

    /// <summary>Available or Installing changed. Can fire on any thread.</summary>
    event Action? Changed;

    /// <summary>Checks now, in the background (the switch was just turned on).</summary>
    void CheckNow();

    /// <summary>
    /// Downloads, checks and starts installing <see cref="Available"/>; only ever called from the user's click. Null when
    /// the installer has started (it closes and restarts the app), otherwise why nothing was installed.
    /// </summary>
    Task<string?> InstallAsync();
}
