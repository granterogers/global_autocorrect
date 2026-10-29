using System.IO;
using GlobalAutocorrect.App.Models;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.Core.Dictionary;
using GlobalAutocorrect.Core.Settings;
using GlobalAutocorrect.Llm;

namespace GlobalAutocorrect.App.Tests.Settings;

/// <summary>Settings, dictionary and status held in memory (dictionary in a temp folder), so view models run without the app.</summary>
internal sealed class FakeServices : ISettingsService, IStatusSource, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GlobalAutocorrect.App.Tests", Guid.NewGuid().ToString("N"));

    public FakeServices()
    {
        Directory.CreateDirectory(_dir);
        Dictionary = new PersonalDictionary(Path.Combine(_dir, "dictionary.txt"));
    }

    public string Dir => _dir;
    public AppSettings Settings { get; set; } = new();
    public PersonalDictionary Dictionary { get; }
    public int Saves { get; private set; }

    public void Update(AppSettings settings)
    {
        Settings = settings;
        Saves++;
        Changed?.Invoke();
        StateChanged?.Invoke();
    }

    public event Action? Changed;
    public event Action? StateChanged;

    public bool Paused => Settings.Paused;
    public ServerState ModelState { get; set; } = ServerState.Ready;
    public ServerInfo? ModelInfo { get; set; } = new(true, "CUDA0 (NVIDIA RTX PRO 2000)", 43, 43, false);
    public double TypicalLatencyMs { get; set; } = 340;
    public long CorrectionsToday { get; set; } = 57;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>A model installer that does what the test tells it to.</summary>
internal sealed class FakeInstaller : IModelInstaller
{
    public bool Installed { get; set; }
    public Exception? FailWith { get; set; }
    public int Installs { get; private set; }

    public bool IsInstalled(string modelFile) => Installed;

    public Task InstallAsync(IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        Installs++;
        if (FailWith is not null)
        {
            var e = FailWith;
            FailWith = null;   // works on the second try
            throw e;
        }
        const long Gb = 1024L * 1024 * 1024;
        progress.Report(new DownloadProgress("Language model", (long)(2.1 * Gb), (long)(5.6 * Gb)));
        Installed = true;
        return Task.CompletedTask;
    }
}
