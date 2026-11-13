namespace GlobalAutocorrect.Core.Telemetry;

/// <summary>
/// D126, user request "catch potential bugs … so much weird stuff happens during the day": what the app did in the last
/// <see cref="KeepMs"/>, in memory only, so that when the user presses the report shortcut right after something went
/// wrong, the moments before it can be saved and traced. Lines are reasons, counts and positions, never text (8.6): every
/// line comes from the diagnostics log's own events or from a fix's lengths and way of writing.
/// </summary>
public sealed class RecentEvents(Func<long> nowMs, int keepMs = RecentEvents.KeepMs, int maxLines = 400)
{
    public const int KeepMs = 90_000;

    private readonly Lock _lock = new();
    private readonly Queue<(long At, DateTime Wall, string Line)> _lines = new();

    public void Add(string line)
    {
        long now = nowMs();
        lock (_lock)
        {
            _lines.Enqueue((now, DateTime.Now, line));
            Trim(now);
        }
    }

    /// <summary>The kept lines, oldest first, each with its time and how long before now it happened.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        long now = nowMs();
        lock (_lock)
        {
            Trim(now);
            return _lines.Select(l => $"{l.Wall:HH:mm:ss.fff} (-{(now - l.At) / 1000.0:0.0}s) {l.Line}").ToList();
        }
    }

    private void Trim(long now)
    {
        while (_lines.Count > 0 && (now - _lines.Peek().At > keepMs || _lines.Count > maxLines)) _lines.Dequeue();
    }
}
