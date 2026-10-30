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

    /// <summary>
    /// One line per problem worth explaining later (a failed verify): app name, kind, character codes. Never typed text
    /// (8.6). Kept small: the file is cut in half past 256 KB.
    /// </summary>
    public static void Diagnose(string line)
    {
        // One writer at a time: lines come from background tasks, and a collision used to drop a line silently.
        lock (DiagnoseLock)
        try
        {
            string path = Path.Combine(DataDir, "diagnostics.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
            {
                var keep = File.ReadAllLines(path);
                File.WriteAllLines(path, keep[(keep.Length / 2)..]);
            }
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly Lock DiagnoseLock = new();

    /// <summary>D64: timings.json, typical and slowest-5% duration of each stage since the app started. Durations only.</summary>
    private void SaveTimings()
    {
        var snapshot = _counters.Timings.Snapshot();
        if (snapshot.Count == 0) return;
        try { File.WriteAllText(Path.Combine(DataDir, "timings.json"), _counters.Timings.ToJson()); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Codes(string s) => string.Join(' ', s.Take(24).Select(c => $"{(int)c:X4}"));
    private NgramModel? _ngram;
    private readonly SettingsStore _store = new(Path.Combine(DataDir, "settings.json"));
    private readonly Counters _counters = new();
    private readonly DailyCounts _daily = new(Path.Combine(DataDir, "counters.json"));
    private KeyboardHook? _hook;
    private EngineLoop? _loop;
    private UiaWorker? _uia;
    private FocusTracker? _focus;
    private HotkeyManager? _hotkeys;
    private LlmRuntime? _llm;
    private EarlyReload? _reload;
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
    public long CorrectionsToday => _daily.CorrectionsToday(_counters);
    public Counters Counters => _counters;

    // Feature B (7.x) and the overlays (4.7, 7.6).
    public UserHistory History { get; } = new(Path.Combine(DataDir, "history.txt"));
    public Predictor? Predictor { get; private set; }
    public UiaWorker? Uia => _uia;
    public KeyboardHook? Hook => _hook;
    public event Action? SuggestionAcceptedByTab;


    /// <summary>Types the accepted suggestion as our own input, on the engine thread (7.4).</summary>
    public void AcceptSuggestion(string completion) => _loop?.Post(e => e.InsertAtCaret(completion));

    /// <summary>Paused/resumed, model loaded or unloaded: the tray and Home page refresh.</summary>
    public event Action? StateChanged;
    /// <summary>A shortcut was pressed but couldn't act (e.g. undo in another field, D9). Plain-language message.</summary>
    public event Action<string>? Notice;

    /// <summary>Shows a tray notification (D58: an update is available).</summary>
    public void ShowNotice(string message) => Notice?.Invoke(message);
    public event Action<GlobalAutocorrect.Core.Write.CorrectionNotice>? Corrected;
    public event Action<KeyInput>? KeyProcessed;

    public async Task StartAsync()
    {
        _ui = SynchronizationContext.Current;
        Directory.CreateDirectory(DataDir);
        Settings = _store.Load();
        Dictionary.AutoLearnEnabled = Settings.AutoLearnWords;

        _speller = HunspellSpellChecker.FromAssets(AssetsDir);
        _suggester = SymSpellSuggester.FromAssets(AssetsDir);
        _ngram = NgramModel.FromAssets(AssetsDir);
        Predictor = new Predictor(_ngram, History) { Enabled = Settings.PredictionEnabled };

        string build = JsonDocument.Parse(File.ReadAllText(Path.Combine(AssetsDir, "manifest.json"))).RootElement.GetProperty("llama").GetProperty("build").GetString()!;
        var host = new LlamaServerHost(new ServerOptions(
            Path.Combine(CacheDir, "llama", build, "cuda", "llama-server.exe"),
            Path.Combine(CacheDir, "models", Settings.Model),
            Path.Combine(CacheDir, "llama", build, "cpu", "llama-server.exe"),
            CpuModelPath: Path.Combine(CacheDir, "models", Settings.FallbackModel),
            ExtraArgs: ServerFlags.LookupDecoding));   // D56
        _llm = new LlmRuntime(host, SystemClock.Instance, () => PromptTemplate.LoadOrDefault(Settings.PromptFile)) { IdleUnloadMinutes = Settings.IdleUnloadMinutes };

        _uia = new UiaWorker();
        Target = new UiaTextTarget(_uia, TargetGate.Create(new AppExclusions(Settings.ExcludedApps ?? AppExclusions.Defaults)), Diagnose, _counters.Timings);
        _hook = new KeyboardHook(untaggedInjectedIsPhysical: Environment.GetEnvironmentVariable("GA_E2E") == "1");
        _hook.Start();

        _loop = new EngineLoop(BuildEngine(), _hook);
        _loop.SuggestionAccepted += () => SuggestionAcceptedByTab?.Invoke();
        // D67: after an idle unload, start reloading as soon as focus lands in a field the app corrects, or on the first key.
        _reload = new EarlyReload(_llm, SystemClock.Instance, () => Settings.Paused, UiaTextTarget.OwnWindowInForeground,
            () => Target?.FocusIsCorrectable() == true, _counters.Timings);
        _llm.Loaded += _ => StateChanged?.Invoke();   // a reload between maintenance passes: the tray and Home page refresh
        _loop.KeyProcessed += k =>
        {
            // 5.6.b: reload on the next keystroke, in the background. Never under a mock model (E2E): that started real
            // servers the tests then orphaned.
            if (k.Kind == KeyKind.Char && !MockedModel) _reload.OnKey();
            KeyProcessed?.Invoke(k);
        };
        Corrected += n => { if (n.Layer == GlobalAutocorrect.Core.Write.Layer.Ai) _reload.OnAiFix(); };   // D67 timing
        _loop.Post(e => e.SetTarget(Target));
        _loop.Start();

        _focus = new FocusTracker(_uia);
        _focus.FocusChanged += _ =>
        {
            _loop.Feed(new KeyInput(KeyKind.FocusChange, '\0', _hook.LatestKeySeq, SystemClock.Instance.NowMs));
            if (!MockedModel) _reload.OnFocusChanged();   // D67
        };

        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += OnHotkey;
        RegisterShortcuts();

        _daily.Load();
        // E2E reads desyncs from counters.json before checking text (11.3.d), so save it every second there.
        var every = Environment.GetEnvironmentVariable("GA_E2E") == "1" ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
        _maintenance = new Timer(_ => _ = MaintainAsync(), null, every, every);
        if (!Settings.Paused && !MockedModel) _ = _llm.StartAsync().ContinueWith(_ => StateChanged?.Invoke(), TaskScheduler.Default);
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

    private static bool MockedModel =>
        Environment.GetEnvironmentVariable("GA_E2E") == "1" && Environment.GetEnvironmentVariable("GA_E2E_LLM_URL") is not null;

    /// <summary>D57: the app the field being learnt from belongs to (the engine has just read it), or null.</summary>
    private string? CurrentApp => Target?.LastSnapshot?.ProcessName;

    private CorrectionEngine BuildEngine()
    {
        _engineOptions = Settings.ToEngineOptions(new EngineOptions());
        var engine = new CorrectionEngine(_engineOptions, new EngineDeps(
            SystemClock.Instance, _hook!, _speller!, _suggester!, ModelClient(),
            Dictionary.Contains, Dictionary.Add, _counters,
            ObserveWord: w => History.Observe(w, CurrentApp),  // 7.3, gated in the engine (8.3); per app, D57
            NoteUnrecognised: Dictionary.NoteUnrecognised,     // 8.5
            ObservePhrase: (a, b, w) => History.ObservePhrase(a, b, w, CurrentApp),   // D40, D57
            PairCount: _ngram is null ? null : _ngram.PairCount,    // D37, D38
            WordCount: _ngram is null ? null : _ngram.CountOf))     // D37
        { Paused = Settings.Paused };
        engine.Corrected += n => Corrected?.Invoke(n);
        // Screen tests only (GA_E2E=1, synthetic text): each write as code points, so a CI failure shows what was written.
        // Never in normal use (8.6).
        if (Environment.GetEnvironmentVariable("GA_E2E") == "1")
        {
            engine.Corrected += n => Diagnose($"write: {n.Layer} dist={n.CaretDistance} retype={n.Retyped} [{Codes(n.Original)}] -> [{Codes(n.Replacement)}] typed={engine.Session.Length}");
            engine.WriteDropped += (layer, reason) => Diagnose($"dropped: {layer} {reason}");
            engine.Session.Restarted += reason => Diagnose($"session: restart {reason}");
        }
        return engine;
    }

    public void SetPaused(bool paused)
    {
        UpdateSettings(Settings with { Paused = paused });
    }

    /// <summary>
    /// Saves new settings and applies only what changed. Pausing just flips the running engine (8.2); the engine is
    /// rebuilt only when correction options change, so undo and the desync list survive ordinary changes (4.6.d, 4.8).
    /// </summary>
    public void UpdateSettings(AppSettings s)
    {
        var old = Settings;
        Settings = s;
        try { _store.Save(s); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notice?.Invoke("Your settings couldn't be saved, so they'll only last until Global Autocorrect closes.");
        }
        Dictionary.AutoLearnEnabled = s.AutoLearnWords;
        if (Predictor is not null) Predictor.Enabled = s.PredictionEnabled;   // 7.7
        if (_llm is not null) _llm.IdleUnloadMinutes = s.IdleUnloadMinutes;
        if (_loop is not null)
        {
            bool exclusionsChanged = !(old.ExcludedApps ?? AppExclusions.Defaults).SequenceEqual(s.ExcludedApps ?? AppExclusions.Defaults);
            if (exclusionsChanged && _uia is not null)
                Target = new UiaTextTarget(_uia, TargetGate.Create(new AppExclusions(s.ExcludedApps ?? AppExclusions.Defaults)), Diagnose, _counters.Timings);
            var options = s.ToEngineOptions(new EngineOptions());
            if (options != _engineOptions) _loop.Replace(BuildEngine());   // BuildEngine records the new options
            if (exclusionsChanged)
            {
                var target = Target;
                _loop.Post(e => e.SetTarget(target));
            }
            bool paused = s.Paused;
            _loop.Post(e => e.Paused = paused);
        }
        if (s.PauseShortcut != old.PauseShortcut || s.UndoShortcut != old.UndoShortcut) RegisterShortcuts();
        StateChanged?.Invoke();
    }

    private EngineOptions? _engineOptions;
    private SynchronizationContext? _ui;

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
        // Hotkeys arrive on the hotkey thread; settings (and re-registering shortcuts) belong on the UI thread, or the
        // hotkey thread would wait on itself (found by the audit).
        if (name == "pause")
        {
            if (_ui is not null) _ui.Post(_ => SetPaused(!Settings.Paused), null);   // 8.2
            else SetPaused(!Settings.Paused);
        }
        else if (name == "undo")
        {
            _loop?.Undo(global: true, r =>   // 4.8.b, once the shortcut's keys are released
            {
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
            SaveTimings();
            History.Save();
            if (_llm is not null && _llm.Host.State == ServerState.Ready && _loop is not null)
                _loop.Post(e => { if (e.TypicalAiLatencyMs > 0) TypicalLatencyMs = e.TypicalAiLatencyMs; });   // median of recent (9.3.a)
            if (ModelState != before) StateChanged?.Invoke();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }   // a timer must never crash the app
    }

    public async ValueTask DisposeAsync()
    {
        _maintenance?.Dispose();
        _daily.Save(_counters);
        try { History.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // keep the last few seconds of counts
        _hotkeys?.Dispose();
        _focus?.Dispose();
        _loop?.Dispose();
        _hook?.Dispose();
        _uia?.Dispose();
        if (_llm is not null) await _llm.DisposeAsync();
    }
}

/// <summary>
/// "Corrections today" (9.3.a) and a running record of daily counts (6.5), across restarts and across midnight.
/// Counts only, never text (8.6). File shape: { "Date", "Counts" (today), "Days": { date: counts } }.
/// </summary>
public sealed class DailyCounts(string path, Func<DateTime>? now = null)
{
    private sealed record Saved(string Date, Dictionary<string, long> Counts, Dictionary<string, Dictionary<string, long>>? Days);

    private readonly Func<DateTime> _now = now ?? (() => DateTime.Now);
    private Dictionary<string, Dictionary<string, long>> _days = [];
    private Dictionary<string, long> _baseline = [];   // today's counts from earlier sessions
    private Dictionary<string, long> _offset = [];     // this session's counters at the start of today
    private string? _date;

    private string Today => _now().ToString("yyyy-MM-dd");

    public IReadOnlyDictionary<string, Dictionary<string, long>> Days => _days;

    public void Load()
    {
        _date = Today;
        try
        {
            if (!File.Exists(path)) return;
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path));
            if (s is null) return;
            _days = s.Days ?? [];
            if (s.Date is not null && !_days.ContainsKey(s.Date)) _days[s.Date] = s.Counts;   // older file shape
            _baseline = _days.TryGetValue(_date, out var today) ? new(today) : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Today's counts: earlier sessions today plus this session since midnight (or since start).</summary>
    public Dictionary<string, long> TodayCounts(Counters c)
    {
        Roll(c);
        return c.Snapshot().ToDictionary(k => k.Key, k => _baseline.GetValueOrDefault(k.Key) + k.Value - _offset.GetValueOrDefault(k.Key));
    }

    public long CorrectionsToday(Counters c)
    {
        var t = TodayCounts(c);
        return t.GetValueOrDefault("instantCorrections") + t.GetValueOrDefault("aiCorrections");
    }

    public void Save(Counters c)
    {
        var today = TodayCounts(c);
        _days[_date!] = today;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new Saved(_date!, today, _days)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>At midnight, close yesterday and start today from the counters' current values.</summary>
    private void Roll(Counters c)
    {
        _date ??= Today;
        if (_date == Today) return;
        var snap = c.Snapshot();
        _days[_date] = snap.ToDictionary(k => k.Key, k => _baseline.GetValueOrDefault(k.Key) + k.Value - _offset.GetValueOrDefault(k.Key));
        _offset = snap;
        _baseline = [];
        _date = Today;
    }
}