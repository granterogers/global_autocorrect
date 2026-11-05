using GlobalAutocorrect.Llm;

namespace GlobalAutocorrect.App.Services;

/// <summary>
/// The live numbers the Home and About pages show (9.3.a). The real source is <see cref="AppHostServices"/>; the screenshot
/// tool (9.5) supplies a fake with demo values.
/// </summary>
public interface IStatusSource
{
    bool Paused { get; }
    ServerState ModelState { get; }
    ServerInfo? ModelInfo { get; }
    double TypicalLatencyMs { get; }
    long CorrectionsToday { get; }

    /// <summary>D97: programs the app is doing less in because reading their fields is slow.</summary>
    IReadOnlyList<string> LightApps => [];

    /// <summary>Raised when any of the above may have changed. Can fire on any thread.</summary>
    event Action? StateChanged;
}
