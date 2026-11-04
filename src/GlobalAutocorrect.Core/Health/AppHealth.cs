using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlobalAutocorrect.Core.Health;

/// <summary>How the app writes in one program (D81).</summary>
public enum AppMode
{
    /// <summary>Every fix the rules allow.</summary>
    Normal,

    /// <summary>The program has surprised us: only fixes close to the caret, on the caret's own line.</summary>
    Careful,
}

/// <summary>
/// D81: what happened when the app wrote in one program, as counts only (8.6: never text, never which words). Counts fade
/// by <see cref="AppHealth.DailyFade"/> each day, so old trouble is forgotten as a program behaves again.
/// </summary>
public sealed class AppStats
{
    /// <summary>Fixes that landed and were verified.</summary>
    public double Writes { get; set; }

    /// <summary>Fixes whose check afterwards found the field not as expected (4.6.d).</summary>
    public double VerifyFailures { get; set; }

    /// <summary>Times the field kept disagreeing with the typed record and could not be resynced (the retry loop D81 stops).</summary>
    public double Stuck { get; set; }

    /// <summary>Fixes the user undid (4.8).</summary>
    public double Undos { get; set; }

    /// <summary>D89: fixes made by stepping back with the arrow keys whose check afterwards failed.</summary>
    public double ArrowFailures { get; set; }

    public AppStats Plus(AppStats o) => new()
    {
        Writes = Writes + o.Writes, VerifyFailures = VerifyFailures + o.VerifyFailures, Stuck = Stuck + o.Stuck, Undos = Undos + o.Undos,
        ArrowFailures = ArrowFailures + o.ArrowFailures,
    };

    internal void Fade(double by)
    {
        Writes *= by; VerifyFailures *= by; Stuck *= by; Undos *= by; ArrowFailures *= by;
    }

    internal double Events => Writes + VerifyFailures + Stuck + Undos + ArrowFailures;
}

/// <summary>
/// D81, user request "keep a record of what's being changed, notice when something strange happens and correct for it,
/// so it improves over time, for all users". Each install keeps, per program (process name), how its fixes went
/// (<see cref="AppStats"/>, counts only) and decides from that how carefully to write there (<see cref="ModeFor"/>).
/// The shipped profiles (<c>assets/app-profiles.json</c>) are the same counts learned on other PCs and published to the
/// source (scripts/publish-learning.ps1), so a new install starts from what earlier ones learned; its own experience is
/// added on top and can outweigh them either way.
/// </summary>
public sealed class AppHealth
{
    /// <summary>Each new day every count is multiplied by this (a week halves it), so a program that behaves again recovers.</summary>
    public const double DailyFade = 0.9;

    private readonly Lock _lock = new();
    private readonly string? _path;
    private readonly Func<DateOnly> _today;
    private Dictionary<string, AppStats> _local = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, AppStats> _shipped = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly _day;
    private bool _dirty;

    public AppHealth(string? path = null, Func<DateOnly>? today = null)
    {
        _path = path;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        _day = _today();
    }

    /// <summary>What a write in <paramref name="app"/> should do now: the shipped profile plus this PC's own counts.</summary>
    public AppMode ModeFor(string app)
    {
        if (string.IsNullOrEmpty(app)) return AppMode.Normal;
        return Decide(StatsFor(app));
    }

    /// <summary>The shipped and local counts together, for <see cref="ModeFor"/> and tests.</summary>
    public AppStats StatsFor(string app)
    {
        lock (_lock)
        {
            FadeIfNewDay();
            var local = _local.GetValueOrDefault(app) ?? new AppStats();
            return _shipped.TryGetValue(app, out var s) ? s.Plus(local) : local.Plus(new AppStats());
        }
    }

    /// <summary>
    /// D81: whether a program's fixes have gone wrong often enough to write there carefully. <paramref name="s"/> holds
    /// faded counts (recent days weigh most), shipped and local together.
    /// </summary>
    public static AppMode Decide(AppStats s)
    {
        // TODO(human): decide when a program should be written to carefully.
        return AppMode.Normal;
    }

    /// <summary>
    /// D89, user request "if it happens once, it never happens again": a fix stepped to with the arrow keys that went wrong
    /// once in a program means its fixes are retyped from then on (delete back to the fix and type it and what followed
    /// again, D46's way), which needs no arrow keys. The count fades like the others, so it lasts about two weeks
    /// (0.9 a day down to 0.25) and is renewed whenever it happens again; shared through the shipped profiles.
    /// </summary>
    public bool RetypeIn(string app) => !string.IsNullOrEmpty(app) && StatsFor(app).ArrowFailures >= RetypeAfter;

    /// <summary>D89: what is left of one arrow-key failure after about two weeks of fading.</summary>
    public const double RetypeAfter = 0.25;

    public void Written(string app) => Note(app, s => s.Writes++);
    public void VerifyFailed(string app) => Note(app, s => s.VerifyFailures++);
    public void ArrowsFailed(string app) => Note(app, s => s.ArrowFailures++);
    public void Stuck(string app) => Note(app, s => s.Stuck++);
    public void Undone(string app) => Note(app, s => s.Undos++);

    private void Note(string app, Action<AppStats> change)
    {
        if (string.IsNullOrEmpty(app)) return;
        lock (_lock)
        {
            FadeIfNewDay();
            if (!_local.TryGetValue(app, out var s)) _local[app] = s = new AppStats();
            change(s);
            _dirty = true;
        }
    }

    private void FadeIfNewDay()
    {
        var today = _today();
        int days = today.DayNumber - _day.DayNumber;
        if (days <= 0) return;
        double by = Math.Pow(DailyFade, days);
        foreach (var s in _local.Values) s.Fade(by);
        _day = today;
        _dirty = true;
    }

    // ---- files ------------------------------------------------------------------------------------------------------

    private sealed record LocalFile(string Day, Dictionary<string, AppStats> Apps);

    /// <summary>The shipped profile: per program, the counts learned elsewhere (D81).</summary>
    public sealed record ProfileFile(string Updated, Dictionary<string, AppStats> Apps);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>Loads this PC's counts and the shipped profiles. A missing or damaged file starts empty.</summary>
    public void Load(string? shippedPath = null)
    {
        lock (_lock)
        {
            if (_path is not null && Read<LocalFile>(_path) is { } f)
            {
                _local = new Dictionary<string, AppStats>(f.Apps, StringComparer.OrdinalIgnoreCase);
                if (DateOnly.TryParse(f.Day, System.Globalization.CultureInfo.InvariantCulture, out var d)) _day = d;
                FadeIfNewDay();
            }
            if (shippedPath is not null && Read<ProfileFile>(shippedPath) is { } p)
                _shipped = new Dictionary<string, AppStats>(p.Apps, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save()
    {
        if (_path is null) return;
        string json;
        lock (_lock)
        {
            if (!_dirty) return;
            json = JsonSerializer.Serialize(new LocalFile(_day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), _local), Json);
            _dirty = false;
        }
        try { File.WriteAllText(_path, json); }
        catch (IOException) { _dirty = true; }
        catch (UnauthorizedAccessException) { _dirty = true; }
    }

    /// <summary>
    /// D81: the shipped profile, built from every PC's published counts (<c>data/learning/*.json</c>, one file per PC,
    /// replaced each time that PC publishes, so nothing is counted twice). Programs with fewer than
    /// <paramref name="minEvents"/> events are left out (too little to learn from, and fewer program names shared).
    /// </summary>
    public static ProfileFile Combine(IEnumerable<IReadOnlyDictionary<string, AppStats>> sources, DateOnly today, double minEvents = 5)
    {
        var apps = new Dictionary<string, AppStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
            foreach (var (k, v) in source) apps[k] = apps.TryGetValue(k, out var s) ? s.Plus(v) : v.Plus(new AppStats());
        // D89: one arrow-key failure is worth sharing on its own (it is what makes another PC retype from the start).
        var kept = apps.Where(a => a.Value.Events >= minEvents || a.Value.ArrowFailures >= RetypeAfter)
            .OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(a => a.Key.ToLowerInvariant(), a => Round(a.Value));
        return new ProfileFile(today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), kept);
    }

    private static AppStats Round(AppStats s) => new()
    {
        Writes = Math.Round(s.Writes, 1), VerifyFailures = Math.Round(s.VerifyFailures, 1), Stuck = Math.Round(s.Stuck, 1), Undos = Math.Round(s.Undos, 1),
        ArrowFailures = Math.Round(s.ArrowFailures, 2),
    };

    /// <summary>This PC's counts (for publishing). A copy.</summary>
    public IReadOnlyDictionary<string, AppStats> Local
    {
        get { lock (_lock) { FadeIfNewDay(); return _local.ToDictionary(k => k.Key, k => k.Value.Plus(new AppStats()), StringComparer.OrdinalIgnoreCase); } }
    }

    public static string ToJson(ProfileFile p) => JsonSerializer.Serialize(p, Json);

    /// <summary>D81: a PC's saved counts (apphealth.json), faded to today, for publishing. Empty if missing or damaged.</summary>
    public static IReadOnlyDictionary<string, AppStats> ReadLocal(string path, DateOnly today)
    {
        var h = new AppHealth(path, () => today);
        h.Load();
        return h.Local;
    }

    public static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
