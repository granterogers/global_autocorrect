namespace GlobalAutocorrect.Core.Telemetry;

/// <summary>
/// D97, user request "this application should be automatically monitored and improving, within the app itself": how
/// long reading a field takes in each program, kept by the app as it runs. Every read is answered on that program's own
/// thread (D94), so where reads are slow the app does less there ("light": suggestions wait for a longer pause and read
/// less text) until reads are quick again. Durations only, per program name (8.6).
/// </summary>
public sealed class ReadCost
{
    /// <summary>The last this-many reads per program are judged.</summary>
    public const int Window = 40;
    /// <summary>Light once the slowest 10% of reads take longer than this; normal again below <see cref="RecoverMs"/>.</summary>
    public const double HeavyMs = PerfReport.SlowReadMs, RecoverMs = 50;
    /// <summary>Not judged on fewer reads than this.</summary>
    public const int MinReads = 10;

    private readonly Dictionary<string, Queue<double>> _reads = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _light = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>A program went light (true) or back to normal (false): for the diagnostics log and the Home page.</summary>
    public event Action<string, bool, double>? Changed;

    public void Record(string app, double ms)
    {
        if (string.IsNullOrEmpty(app) || ms < 0 || double.IsNaN(ms)) return;
        bool? change = null;
        double p90;
        lock (_gate)
        {
            if (!_reads.TryGetValue(app, out var q)) _reads[app] = q = new Queue<double>();
            q.Enqueue(ms);
            if (q.Count > Window) q.Dequeue();
            if (q.Count < MinReads) return;
            var sorted = q.Order().ToArray();
            p90 = sorted[Math.Clamp((int)Math.Ceiling(0.9 * sorted.Length) - 1, 0, sorted.Length - 1)];
            if (p90 > HeavyMs && _light.Add(app)) change = true;
            else if (p90 < RecoverMs && _light.Remove(app)) change = false;
        }
        if (change is { } light) Changed?.Invoke(app, light, p90);
    }

    public bool IsLight(string app)
    {
        lock (_gate) return !string.IsNullOrEmpty(app) && _light.Contains(app);
    }

    /// <summary>The programs the app is doing less in now, by name.</summary>
    public IReadOnlyList<string> LightApps
    {
        get { lock (_gate) return _light.Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
    }
}
