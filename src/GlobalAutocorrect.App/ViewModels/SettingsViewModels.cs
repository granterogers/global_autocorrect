using System.IO;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.App.Tray;

namespace GlobalAutocorrect.App.ViewModels;

/// <summary>Where the settings pages find files on disk. Tests and the screenshot tool pass their own.</summary>
public sealed record SettingsEnvironment(string ModelsDir, string MetricsPath, string NoticePath, StartupRegistration Startup)
{
    /// <summary>The real locations: the model cache, and metrics.json and NOTICE.md next to the exe (6.6, 13.1).</summary>
    public static SettingsEnvironment Default() => new(
        Path.Combine(AppHost.CacheDir, "models"),
        Path.Combine(AppContext.BaseDirectory, "metrics.json"),
        Path.Combine(AppHost.AssetsDir, "NOTICE.md"),
        new StartupRegistration());
}

/// <summary>All the settings pages' view models, created together so the window can refresh them as one.</summary>
public sealed class SettingsViewModels : IDisposable
{
    public SettingsViewModels(ISettingsService settings, IStatusSource status, SettingsEnvironment env, IUpdateService? updates = null)
    {
        Home = new HomeViewModel(settings, status);
        CleanUp = new CleanUpViewModel(settings, env.ModelsDir);
        Prediction = new PredictionViewModel(settings);
        Shortcuts = new ShortcutsViewModel(settings);
        Apps = new AppsViewModel(settings);
        Dictionary = new DictionaryViewModel(settings);
        About = new AboutViewModel(settings, env.MetricsPath, env.NoticePath, env.Startup, updates: updates);
    }

    public HomeViewModel Home { get; }
    public CleanUpViewModel CleanUp { get; }
    public PredictionViewModel Prediction { get; }
    public ShortcutsViewModel Shortcuts { get; }
    public AppsViewModel Apps { get; }
    public DictionaryViewModel Dictionary { get; }
    public AboutViewModel About { get; }

    /// <summary>Settings changed somewhere else (a shortcut, the tray): bring every page up to date.</summary>
    public void RefreshSettings()
    {
        Home.Refresh();
        CleanUp.Refresh();
        Prediction.Refresh();
        Shortcuts.Refresh();
        Apps.Refresh();
        Dictionary.Refresh();
        About.Refresh();
    }

    /// <summary>The live numbers on Home (model state, latency, corrections), polled while the window is open.</summary>
    public void RefreshLive() => Home.Refresh();

    public void Dispose() => Dictionary.Dispose();
}
