using System.IO;
using System.Text.Json;
using GlobalAutocorrect.Core.Ai;
using GlobalAutocorrect.Core.Dictionary;
using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Prediction;
using GlobalAutocorrect.Core.Session;
using GlobalAutocorrect.Core.Settings;
using GlobalAutocorrect.Core.Telemetry;
using GlobalAutocorrect.Llm;
using GlobalAutocorrect.Windows.Hotkeys;
using GlobalAutocorrect.Windows.Input;
using GlobalAutocorrect.Windows.Targets;
using GlobalAutocorrect.Windows.Uia;
using Path = System.IO.Path;

namespace GlobalAutocorrect.App;

/// <summary>
/// Builds and owns Feature A at runtime (10.4): hook → engine loop → UIA target, the model runtime, shortcuts and
/// housekeeping. The UI (tray, settings) talks to the app only through this class.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    /// <summary>All user data (8.6). GA_DATA_DIR overrides it so E2E runs never touch the user's own settings or dictionary.</summary>
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("GA_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlobalAutocorrect");
    public static string CacheDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlobalAutocorrect", "cache");
    public static string AssetsDir { get; } = Path.Combine(AppContext.BaseDirectory, "assets");

    private readonly SettingsStore _store = new(Path.Combine(DataDir, "settings.json"));
    private readonly Counters _counters = new();
    private readonly DailyCounts _daily = new(Path.Combine(DataDir, "counters.json"));
    private KeyboardHook? _hook;
    private EngineLoop? _loop;
    private UiaWorker? _uia;
    private FocusTracker? _focus;
    private HotkeyManager? _hotkeys;
    private LlmRuntime? _llm;
    private Timer? _maintenance;
    private ISpellChecker? _speller;
    private ISuggester? _suggester;

    public AppSettings Settings { get; private set; } = new();
    public PersonalDictionary Dictionary { get; } = new(Path.Combine(DataDir, "dictionary.txt"));
    public UiaTextTarget? Target { get; private set; }
    public bool Paused => Settings.Paused;
    public ServerState ModelState => _llm?.Host.State ?? ServerState.Stopped;
    public ServerInfo? ModelInfo => _llm?.Host.Info;
    public double TypicalLatencyMs { get; private set; }
    public long CorrectionsToday => _daily.Baseline + _counters.Corrections;
    public Counters Counters => _counters;

    // Feature B (7.x) and the overlays (4.7, 7.6).
    public UserHistory History { get; } = new(Path.Combine(DataDir, "history.txt"));
    public Predictor? Predictor { get; private set; }
    public UiaWorker? Uia => _uia;
    public KeyboardHook? Hook => _hook;
    public event Action? SuggestionAcceptedByTab;

    /// <summary>Only real words are learnt for prediction, so typos never come back as suggestions (7.3).</summary>
    public bool IsKnownWord(string w) => _speller?.IsValid(w) == true || Dictionary.Contains(w);

    /// <summary>Types the accepted suggestion as our own input, on the engine thread (7.4).</summary>
    public void AcceptSuggestion(string completion) => _loop?.Post(e => e.InsertAtCaret(completion));

    /// <summary>Paused/resumed, model loaded or unloaded: the tray and Home page refresh.</summary>
    public event Action? StateChanged;
    /// <summary>A shortcut was pressed but couldn't act (e.g. undo in another field, D9). Plain-language message.</summary>
    public event Action<string>? Notice;
    public event Action<GlobalAutocorrect.Core.Write.CorrectionNotice>? Corrected;
    public event Action<KeyInput>? KeyProcessed;

    public async Task StartAsync()
    {
        Directory.CreateDirectory(DataDir);
        Settings = _store.Load();
        Dictionary.AutoLearnEnabled = Settings.AutoLearnWords;

        _speller = HunspellSpellChecker.FromAssets(AssetsDir);
        _suggester = SymSpellSuggester.FromAssets(AssetsDir);
        Predictor = new Predictor(NgramModel.FromAssets(AssetsDir), History) { Enabled = Settings.PredictionEnabled };

        string build = JsonDocument.Parse(File.ReadAllText(Path.Combine(AssetsDir, "manifest.json"))).RootElement.GetProperty("llama").GetProperty("build").GetString()!;
        var host = new LlamaServerHost(new ServerOptions(
            Path.Combine(CacheDir, "llama", build, "cuda", "llama-server.exe"),
            Path.Combine(CacheDir, "models", Settings.Model),
            Path.Combine(CacheDir, "llama", build, "cpu", "llama-server.exe"),
            CpuModelPath: Path.Combine(CacheDir, "models", Settings.FallbackModel)));
        _llm = new LlmRuntime(host, SystemClock.Instance, () => PromptTemplate.Default) { IdleUnloadMinutes = Settings.IdleUnloadMinutes };

        _uia = new UiaWorker();
        Target = new UiaTextTarget(_uia, TargetGate.Create(new AppExclusions(Settings.ExcludedApps ?? AppExclusions.Defaults)));
        _hook = new KeyboardHook(untaggedInjectedIsPhysical: Environment.GetEnvironmentVariable("GA_E2E") == "1");
        _hook.Start();

        _loop = new EngineLoop(BuildEngine(), _hook);
        _loop.SuggestionAccepted += () => SuggestionAcceptedByTab?.Invoke();
        _loop.KeyProcessed += k =>
        {
            if (k.Kind == KeyKind.Char) _llm.NotifyActivity();   // 5.6.b: reload on the next keystroke, in the background
            KeyProcessed?.Invoke(k);
        };
        _loop.Post(e => e.SetTarget(Target));
        _loop.Start();

        _focus = new FocusTracker(_uia);
        _focus.FocusChanged += _ => _loop.Post(e => e.OnInput(new KeyInput(KeyKind.FocusChange, '\0', _hook.LatestKeySeq, SystemClock.Instance.NowMs)));

        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += OnHotkey;
        RegisterShortcuts();

        _daily.Load();
        // E2E reads desyncs from counters.json before checking text (11.3.d), so save it every second there.
        var every = Environment.GetEnvironmentVariable("GA_E2E") == "1" ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
        _maintenance = new Timer(_ => _ = MaintainAsync(), null, every, every);
        bool mocked = Environment.GetEnvironmentVariable("GA_E2E_LLM_URL") is not null && Environment.GetEnvironmentVariable("GA_E2E") == "1";
        if (!Settings.Paused && !mocked) _ = _llm.Host.StartAsync(CancellationToken.None).ContinueWith(_ => StateChanged?.Invoke(), TaskScheduler.Default);
        await Task.CompletedTask;
    }

    /// <summary>
    /// E2E only (11.1.d): with GA_E2E=1, GA_E2E_LLM_URL points the app at a mock model server on loopback instead of
    /// starting llama-server. The client still refuses anything that isn't loopback (3.3).
    /// </summary>
    private ILlmClient? ModelClient()
    {
        string? mock = Environment.GetEnvironmentVariable("GA_E2E") == "1" ? Environment.GetEnvironmentVariable("GA_E2E_LLM_URL") : null;
        return mock is null ? _llm : (_mockClient ??= new LlamaServerClient(new Uri(mock), PromptTemplate.Default));
    }

    private LlamaServerClient? _mockClient;

    private CorrectionEngine BuildEngine()
    {
        var engine = new CorrectionEngine(Settings.ToEngineOptions(new EngineOptions()), new EngineDeps(
            SystemClock.Instance, _hook!, _speller!, _suggester!, ModelClient(),
            Dictionary.Contains, Dictionary.Add, _counters)) { Paused = Settings.Paused };
        engine.Corrected += n => Corrected?.Invoke(n);
        return engine;
    }

    public void SetPaused(bool paused)
    {
        UpdateSettings(Settings with { Paused = paused });
    }

    /// <summary>Saves new settings and applies them: engine options, exclusions, shortcuts, model idle time.</summary>
    public void UpdateSettings(AppSettings s)
    {
        Settings = s;
        _store.Save(s);
        Dictionary.AutoLearnEnabled = s.AutoLearnWords;
        if (Predictor is not null) Predictor.Enabled = s.PredictionEnabled;   // 7.7
        if (_llm is not null) _llm.IdleUnloadMinutes = s.IdleUnloadMinutes;
        if (_uia is not null) Target = new UiaTextTarget(_uia, TargetGate.Create(new AppExclusions(s.ExcludedApps ?? AppExclusions.Defaults)));
        if (_loop is not null)
        {
            _loop.Replace(BuildEngine());
            var target = Target;
            _loop.Post(e => e.SetTarget(target));
        }
        RegisterShortcuts();
        StateChanged?.Invoke();
    }

    private void RegisterShortcuts()
    {
        if (_hotkeys is null) return;
        if (Hotkey.TryParse(Settings.PauseShortcut, out var pause) && !_hotkeys.Register("pause", pause))
            Notice?.Invoke($"{Settings.PauseShortcut} is already used by another app. Choose a different pause shortcut in Settings.");
        if (Hotkey.TryParse(Settings.UndoShortcut, out var undo) && !_hotkeys.Register("undo", undo))
            Notice?.Invoke($"{Settings.UndoShortcut} is already used by another app. Choose a different undo shortcut in Settings.");
    }

    private void OnHotkey(string name)
    {
        if (name == "pause") SetPaused(!Settings.Paused);   // 8.2
        else if (name == "undo")
        {
            _loop?.Post(e =>
            {
                var r = e.Undo(global: true);   // 4.8.b
                if (r == Core.Undo.UndoResult.OtherField) Notice?.Invoke("Switch back to the field with the correction to undo it.");
                else if (r == Core.Undo.UndoResult.TextChanged) Notice?.Invoke("That text has changed since the correction, so there's nothing to undo.");
            });
        }
    }

    private async Task MaintainAsync()
    {
        try
        {
            var before = ModelState;
            if (_llm is not null) await _llm.MaintainAsync(CancellationToken.None);
            _hook?.CheckAlive();   // 12.6
            _daily.Save(_counters);
            History.Save();
            if (_llm is not null && _llm.Host.State == ServerState.Ready && _loop is not null)
                _loop.Post(e => { if (e.LastAiLatencyMs > 0) TypicalLatencyMs = TypicalLatencyMs == 0 ? e.LastAiLatencyMs : TypicalLatencyMs * 0.8 + e.LastAiLatencyMs * 0.2; });
            if (ModelState != before) StateChanged?.Invoke();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _maintenance?.Dispose();
        _daily.Save(_counters);
        _hotkeys?.Dispose();
        _focus?.Dispose();
        _loop?.Dispose();
        _hook?.Dispose();
        _uia?.Dispose();
        if (_llm is not null) await _llm.DisposeAsync();
    }
}

/// <summary>"Corrections today" (9.3.a) across restarts. Counts only (6.5, 8.6).</summary>
internal sealed class DailyCounts(string path)
{
    private sealed record Saved(string Date, Dictionary<string, long> Counts);

    public long Baseline { get; private set; }
    private Dictionary<string, long> _baseline = [];
    private string _date = Today;
    private static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    public void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path));
            if (s?.Date == Today)
            {
                _baseline = s.Counts;
                Baseline = s.Counts.GetValueOrDefault("instantCorrections") + s.Counts.GetValueOrDefault("aiCorrections");
            }
        }
        catch (Exception e) when (e is JsonException or IOException) { }
    }

    public void Save(Counters c)
    {
        if (_date != Today) { _date = Today; _baseline = []; Baseline = 0; }   // new day: start again
        var now = c.Snapshot().ToDictionary(k => k.Key, k => k.Value + _baseline.GetValueOrDefault(k.Key));
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new Saved(_date, now)));
        }
        catch (IOException) { }
    }
}
