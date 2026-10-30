using System.Text.Json;

namespace GlobalAutocorrect.Core.Telemetry;

/// <summary>
/// D64 (12.1, 12.2): how long each stage of a correction takes in real use, as durations only, never text (8.6). Keeps
/// the last <see cref="Keep"/> samples per stage and reports the typical (p50) and slowest-5% (p95) values. Stages:
/// <c>instant.keyToFix</c> and <c>ai.keyToFix</c> (from the key that finished the word or sentence to the fix landing,
/// what the user feels), <c>ai.model</c>, <c>read</c>, <c>write</c> and <c>verify</c>. D67 adds <c>model.load</c>,
/// <c>model.reloadWait</c> and <c>model.reloadKeyToFix</c> (see <c>EarlyReload</c>).
/// </summary>
public sealed class Timings
{
    public const int Keep = 1000;
    private readonly Dictionary<string, Queue<double>> _samples = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void Record(string stage, double ms)
    {
        if (ms < 0 || double.IsNaN(ms)) return;
        lock (_gate)
        {
            if (!_samples.TryGetValue(stage, out var q)) _samples[stage] = q = new Queue<double>();
            q.Enqueue(ms);
            if (q.Count > Keep) q.Dequeue();
        }
    }

    public sealed record Summary(int Count, double P50, double P95, double Max);

    public Dictionary<string, Summary> Snapshot()
    {
        lock (_gate)
        {
            return _samples.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv =>
            {
                var s = kv.Value.OrderBy(x => x).ToArray();
                return new Summary(s.Length, Percentile(s, 0.50), Percentile(s, 0.95), s[^1]);
            }, StringComparer.Ordinal);
        }
    }

    public string ToJson() => JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions { WriteIndented = true });

    private static double Percentile(double[] sorted, double p) => Math.Round(sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)]);
}
