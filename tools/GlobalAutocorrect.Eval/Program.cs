using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using GlobalAutocorrect.Core.Ai;
using GlobalAutocorrect.Core.Eval;

namespace GlobalAutocorrect.Eval;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var opts = ParseOptions(args.Skip(1));
        return args.FirstOrDefault() switch
        {
            "bench" => await Bench(opts),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: Eval bench --server <llama-server.exe> --model <file.gguf> --set <file.jsonl> [--n 50] [--name label] [--out artifacts] [--ngl 99]");
        return 2;
    }

    /// <summary>Spike A (2.4.a): raw model quality and speed on N tuning messages.</summary>
    private static async Task<int> Bench(Dictionary<string, string> o)
    {
        string server = o["server"], model = o["model"], set = o["set"];
        int n = int.Parse(o.GetValueOrDefault("n", "50"));
        string name = o.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(model));
        string outDir = o.GetValueOrDefault("out", "artifacts");
        Directory.CreateDirectory(outDir);

        // Evenly spaced sample, so every noise level and the garbled set are represented (the file is sorted by id).
        var all = File.ReadLines(set).Where(l => l.Trim().Length > 0).ToArray();
        var items = Enumerable.Range(0, Math.Min(n, all.Length)).Select(i => all[(int)((long)i * all.Length / Math.Min(n, all.Length))])
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Select(e => (Id: e.GetProperty("id").GetString()!, Clean: e.GetProperty("clean").GetString()!, Noisy: e.GetProperty("noisy").GetString()!))
            .ToList();

        long vramBefore = GpuMemoryUsedMiB();
        var loadSw = Stopwatch.StartNew();
        await using var srv = await SpikeServer.StartAsync(server, model, int.Parse(o.GetValueOrDefault("ngl", "99")));
        loadSw.Stop();
        Console.WriteLine($"[{name}] loaded in {loadSw.Elapsed.TotalSeconds:F1}s");

        var prompt = PromptTemplate.Default;
        // Warm-up fills the prompt cache with the fixed prefix, as the app does on load.
        await srv.CleanAsync(prompt, "warm up teh cache plese");
        await srv.CleanAsync(prompt, "Second warm-up message.");
        long vramLoaded = GpuMemoryUsedMiB();

        var rows = new List<object>();
        var scores = new List<MessageScore>();
        var lat = new List<double>();
        foreach (var (id, clean, noisy) in items)
        {
            var (text, ms, timings) = await srv.CleanAsync(prompt, noisy);
            var s = Scorer.Score(clean, noisy, text);
            scores.Add(s);
            lat.Add(ms);
            rows.Add(new { id, noisy, clean, output = text, ms = Math.Round(ms), s.NeedFix, s.Fixed, s.CorrectWords, s.FalseEdits, s.Exact,
                promptMs = timings?["prompt_ms"]?.GetValue<double>(), predictedMs = timings?["predicted_ms"]?.GetValue<double>(),
                cached = timings?["cache_n"]?.GetValue<int>() });
            Console.WriteLine($"  {id} {ms,6:F0} ms  fix {s.Fixed}/{s.NeedFix}  false {s.FalseEdits}");
        }
        long vramPeak = GpuMemoryUsedMiB();

        var log = srv.Log;
        string cuda = log.FirstOrDefault(l => l.Contains("using device CUDA0", StringComparison.Ordinal))
                      ?? log.FirstOrDefault(l => l.Contains("CUDA0", StringComparison.Ordinal)) ?? "(no CUDA0 line)";
        string offload = log.FirstOrDefault(l => Regex.IsMatch(l, @"offloaded \d+/\d+ layers")) ?? "(no offload line)";

        var agg = Scorer.Combine(scores);
        var sorted = lat.Order().ToArray();
        var summary = new
        {
            name, model = Path.GetFileName(model), messages = items.Count,
            p50Ms = Percentile(sorted, 0.50), p95Ms = Percentile(sorted, 0.95), meanMs = lat.Average(),
            errorsFixedRate = agg.ErrorsFixedRate, falseEditRate = agg.FalseEditRate, exactMatchRate = agg.ExactMatchRate,
            agg.NeedFix, agg.Fixed, agg.CorrectWords, agg.FalseEdits,
            vramBeforeMiB = vramBefore, vramLoadedMiB = vramLoaded, vramPeakMiB = vramPeak,
            loadSeconds = loadSw.Elapsed.TotalSeconds, cudaLine = cuda.Trim(), offloadLine = offload.Trim(),
            pass = Percentile(sorted, 0.5) < 1500 && agg.ErrorsFixedRate >= 0.90 && agg.FalseEditRate < 0.02,
        };
        string path = Path.Combine(outDir, $"spikeA-{name}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { summary, rows }, SpikeServer.JsonOut));
        Console.WriteLine(JsonSerializer.Serialize(summary, SpikeServer.JsonOut));
        Console.WriteLine($"wrote {path}");
        return 0;
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        double rank = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return Math.Round(sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo));
    }

    private static long GpuMemoryUsedMiB()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return long.TryParse(s.Split('\n')[0], out var v) ? v : -1;
        }
        catch (System.ComponentModel.Win32Exception) { return -1; }
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? key = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal)) { key = a[2..]; d[key] = "true"; }
            else if (key is not null) { d[key] = a; key = null; }
        }
        return d;
    }
}
