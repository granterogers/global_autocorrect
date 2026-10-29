using GlobalAutocorrect.Core.Dictionary;
using GlobalAutocorrect.Core.Settings;
using GlobalAutocorrect.Llm;

namespace GlobalAutocorrect.App.Services;

/// <summary>Adapts the running <see cref="AppHost"/> to the small interfaces the settings and first-run windows use.</summary>
public sealed class AppHostServices(AppHost host) : ISettingsService, IStatusSource
{
    public AppSettings Settings => host.Settings;
    public PersonalDictionary Dictionary => host.Dictionary;
    public void Update(AppSettings settings) => host.UpdateSettings(settings);

    public bool Paused => host.Paused;
    public ServerState ModelState => host.ModelState;
    public ServerInfo? ModelInfo => host.ModelInfo;
    public double TypicalLatencyMs => host.TypicalLatencyMs;
    public long CorrectionsToday => host.CorrectionsToday;

    // UpdateSettings, SetPaused and model changes all raise StateChanged, so one event serves both interfaces.
    public event Action? StateChanged
    {
        add => host.StateChanged += value;
        remove => host.StateChanged -= value;
    }

    event Action? ISettingsService.Changed
    {
        add => host.StateChanged += value;
        remove => host.StateChanged -= value;
    }
}
