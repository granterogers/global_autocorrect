namespace GlobalAutocorrect.Core.Telemetry;

/// <summary>One sample of how the app is doing: memory, CPU, GC pauses, and how often and how slowly it reads fields.</summary>
public sealed record PerfSample(double AtMs, double PrivateMb, double WorkingSetMb, double CpuMs, double GcPauseMs, int Gen2,
    double ModelMb, double FreeRamMb, long Reads, long Keys);

/// <summary>
/// D94, user request "keep track of performance, like memory usage... sometimes it takes ages for me to type letters":
/// numbers only, never text (8.6). Between two samples: CPU share, GC pause, reads per minute, reads per key, and whether
/// anything says typing was slowed.
/// </summary>
public static class PerfReport
{
    /// <summary>Above these, the line is marked slow: a key reaching the app late, or reads that hold the typed-in app up.</summary>
    public const double SlowKeyMs = 50, SlowReadMs = 100, SlowGcPauseShare = 0.02;

    public static string Line(PerfSample from, PerfSample to, Timings.Summary? read, Timings.Summary? keyDelay)
    {
        double minutes = Math.Max(1e-6, (to.AtMs - from.AtMs) / 60_000);
        double elapsedMs = Math.Max(1, to.AtMs - from.AtMs);
        double cpuPct = 100 * (to.CpuMs - from.CpuMs) / elapsedMs / Environment.ProcessorCount;
        double gcMs = to.GcPauseMs - from.GcPauseMs;
        long reads = to.Reads - from.Reads, keys = to.Keys - from.Keys;
        var slow = new List<string>();
        if (keys > 0 && keyDelay is { P95: > SlowKeyMs }) slow.Add("keys-late");
        if (reads > 0 && read is { P95: > SlowReadMs }) slow.Add("reads-slow");
        if (gcMs / elapsedMs > SlowGcPauseShare) slow.Add("gc-pauses");
        return $"perf: privateMB={to.PrivateMb:0} workingSetMB={to.WorkingSetMb:0} cpuPct={cpuPct:0.0} gcPauseMs={gcMs:0} gen2={to.Gen2 - from.Gen2} " +
               $"modelMB={to.ModelMb:0} freeRamMB={to.FreeRamMb:0} keys={keys} readsPerMin={reads / minutes:0} readsPerKey={(keys == 0 ? 0 : (double)reads / keys):0.00} " +
               $"readP95={read?.P95 ?? 0:0} keyDelayP95={keyDelay?.P95 ?? 0:0} {(slow.Count == 0 ? "ok" : "SLOW " + string.Join(',', slow))}";
    }
}
