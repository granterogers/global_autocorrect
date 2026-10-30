using GlobalAutocorrect.Core.Prediction;
using System.Text;
using System.Text.Json;
using GlobalAutocorrect.Core.Ai;
using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Eval;
using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Llm;

namespace GlobalAutocorrect.Eval;

/// <summary>
/// `run --set holdout|tuning`: scores the whole pipeline (6.2) and writes metrics.json plus a short report (11.1.c, 6.3, 6.6).
/// The holdout is never used for tuning (6.1.e); tuning runs use `--set tuning`.
/// </summary>
internal static class RunCommand
{
    public const double TargetFixed = 0.95, TargetFalse = 0.01;   // 6.4

    public static async Task<int> Run(Dictionary<string, string> o, string repo)
    {
        string set = o.GetValueOrDefault("set", "holdout");
        string outDir = o.GetValueOrDefault("out", "artifacts");
        string cache = o.GetValueOrDefault("cache", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlobalAutocorrect", "cache"));
        string modelFile = o.GetValueOrDefault("model", "gemma-4-E4B-it-Q5_K_M.gguf");
        int limit = int.Parse(o.GetValueOrDefault("n", "0"));
        Directory.CreateDirectory(outDir);

        var items = File.ReadLines(Path.Combine(repo, "data", "eval", set + ".jsonl")).Where(l => l.Trim().Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Select(e => new EvalItem(e.GetProperty("id").GetString()!, e.GetProperty("clean").GetString()!, e.GetProperty("noisy").GetString()!,
                e.TryGetProperty("level", out var lv) ? lv.GetString()! : "?",
                e.TryGetProperty("ops", out var ops) ? ops.EnumerateArray().Select(x => x.GetString()!).ToArray() : []))
            .ToList();
        var trace = o.TryGetValue("trace", out var t) ? t.Split(',').ToHashSet() : [];
        if (trace.Count > 0) items = items.Where(i => trace.Contains(i.Id)).ToList();   // tracing: just those messages
        if (limit > 0) items = items.Take(limit).ToList();

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "scripts", "manifest.json"))).RootElement;
        string build = manifest.GetProperty("llama").GetProperty("build").GetString()!;
        await using var host = new LlamaServerHost(new ServerOptions(
            Path.Combine(cache, "llama", build, "cuda", "llama-server.exe"), Path.Combine(cache, "models", modelFile),
            Path.Combine(cache, "llama", build, "cpu", "llama-server.exe")));
        if (!await host.StartAsync(CancellationToken.None)) { Console.Error.WriteLine("model server did not start; run scripts/setup.ps1 then scripts/verify.ps1"); return 1; }
        using var client = new LlamaServerClient(host.BaseAddress!, PromptTemplate.LoadOrDefault(o.GetValueOrDefault("prompt")), apiKey: host.ApiKey);
        await client.CleanAsync("warm up teh cache", CancellationToken.None);

        var options = EvalOptions(o);
        var ngram = NgramModel.FromAssets(Path.Combine(repo, "assets"));
        var runner = new PipelineRunner(HunspellSpellChecker.FromAssets(Path.Combine(repo, "assets")),
            SymSpellSuggester.FromAssets(Path.Combine(repo, "assets")), client, options,
            pairCount: ngram.PairCount, wordCount: ngram.CountOf) { TraceIds = trace, Quirks = o.TryGetValue("realistic", out var rq) ? (rq == "true" ? ["all"] : rq.Split(",").ToHashSet()) : [] };

        var results = new List<ItemResult>();
        foreach (var item in items)
        {
            var r = await runner.RunAsync(item, CancellationToken.None);
            results.Add(r);
            Console.WriteLine($"{item.Id,-6} {item.Level,-8} fix {r.Score.Fixed}/{r.Score.NeedFix} false {r.Score.FalseEdits}{(r.Score.Exact ? "  exact" : "")}");
        }

        if (o.ContainsKey("dump"))   // tuning analysis: every message with its result (never used with the holdout, 6.1.e)
        {
            if (set == "holdout") { Console.Error.WriteLine("--dump is for the tuning set only"); return 2; }
            var raw = new Dictionary<string, string>();
            foreach (var r in results) raw[r.Item.Id] = (await client.CleanAsync(r.Item.Noisy, CancellationToken.None)).Text;   // whole message, no rules
            await File.WriteAllLinesAsync(Path.Combine(outDir, $"items-{set}.jsonl"), results.Select(r => JsonSerializer.Serialize(new
                { r.Item.Id, r.Item.Level, ops = r.Item.Ops, r.Score.NeedFix, r.Score.Fixed, r.Score.FalseEdits, noisy = r.Item.Noisy, output = r.Output, raw = raw[r.Item.Id], clean = r.Item.Clean, counters = r.Counters })));
        }
        var agg = Scorer.Combine(results.Select(r => r.Score));
        var lat = runner.Latencies.Order().ToArray();
        var byLevel = results.GroupBy(r => r.Item.Level).OrderBy(g => g.Key).ToDictionary(g => g.Key, g =>
        {
            var a = Scorer.Combine(g.Select(r => r.Score));
            return new { messages = a.Messages, errorsFixedRate = a.ErrorsFixedRate, falseEditRate = a.FalseEditRate, exactMatchRate = a.ExactMatchRate };
        });
        var worst = results.OrderByDescending(r => (r.Score.NeedFix - r.Score.Fixed) + 3 * r.Score.FalseEdits)
            .ThenBy(r => r.Item.Id).Take(20)
            .Select(r => new { r.Item.Id, category = Category(r), r.Score.NeedFix, r.Score.Fixed, r.Score.FalseEdits, noisy = r.Item.Noisy, output = r.Output, clean = r.Item.Clean })
            .ToArray();

        var metrics = new
        {
            set, model = modelFile, llamaBuild = build, date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            messages = agg.Messages, errorsFixedRate = agg.ErrorsFixedRate, falseEditRate = agg.FalseEditRate, exactMatchRate = agg.ExactMatchRate,
            needFix = agg.NeedFix, @fixed = agg.Fixed, correctWords = agg.CorrectWords, falseEdits = agg.FalseEdits,
            latencyP50Ms = Percentile(lat, 0.5), latencyP95Ms = Percentile(lat, 0.95), modelRequests = lat.Length,
            gpu = host.Info?.Device, offloaded = host.Info is { } i ? $"{i.OffloadedLayers}/{i.TotalLayers}" : null,
            targets = new { errorsFixed = TargetFixed, falseEdits = TargetFalse },
            meetsTargets = agg.ErrorsFixedRate >= TargetFixed && agg.FalseEditRate < TargetFalse,
            options = new { options.PauseMs, options.Rules.MaxNormalisedDistance, options.Rules.MaxChangedWordRatio },
            byLevel, worst,
        };
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        string suffix = set == "holdout" ? "" : "-" + set;
        if (trace.Count > 0) return 0;   // a trace run doesn't overwrite the metrics
        await File.WriteAllTextAsync(Path.Combine(outDir, $"metrics{suffix}.json"), JsonSerializer.Serialize(metrics, json));
        await File.WriteAllTextAsync(Path.Combine(outDir, $"eval-report{suffix}.md"), Report(metrics.set, modelFile, agg, lat, byLevel.ToDictionary(k => k.Key, k => (object)k.Value), results, worst.Length));
        Console.WriteLine($"\n{set}: errors fixed {agg.ErrorsFixedRate:P1} (target ≥95%), false edits {agg.FalseEditRate:P2} (target <1%), exact {agg.ExactMatchRate:P0}, p50 {Percentile(lat, 0.5)} ms, p95 {Percentile(lat, 0.95)} ms");
        return 0;
    }

    /// <summary>Tuning knobs (4.5.e); only ever changed against the tuning set.</summary>
    private static EngineOptions EvalOptions(Dictionary<string, string> o)
    {
        var d = new EngineOptions();
        var rules = d.Rules with
        {
            MaxNormalisedDistance = double.Parse(o.GetValueOrDefault("max-dist", d.Rules.MaxNormalisedDistance.ToString(System.Globalization.CultureInfo.InvariantCulture)), System.Globalization.CultureInfo.InvariantCulture),
            MaxChangedWordRatio = double.Parse(o.GetValueOrDefault("max-ratio", d.Rules.MaxChangedWordRatio.ToString(System.Globalization.CultureInfo.InvariantCulture)), System.Globalization.CultureInfo.InvariantCulture),
        };
        return d with { Rules = rules, PauseMs = int.Parse(o.GetValueOrDefault("pause-ms", d.PauseMs.ToString())) };
    }

    private static string Category(ItemResult r) =>
        r.Item.Level == "Garbled" ? "garbled" : r.Item.Ops.Count == 0 ? $"clean ({r.Item.Level})" : $"{r.Item.Level}: {string.Join(", ", r.Item.Ops.Distinct())}";

    private static string Report(string set, string model, Aggregate a, double[] lat, Dictionary<string, object> byLevel, List<ItemResult> results, int worstN)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Evaluation: {set} set");
        sb.AppendLine();
        sb.AppendLine($"Model `{model}`, {a.Messages} messages, whole pipeline (instant layer + AI layer + edit rules + timing), {DateTime.UtcNow:yyyy-MM-dd}.");
        sb.AppendLine();
        sb.AppendLine("| Metric | Result | Target (6.4) |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| Errors fixed | {a.ErrorsFixedRate:P1} ({a.Fixed}/{a.NeedFix}) | ≥ 95% {(a.ErrorsFixedRate >= TargetFixed ? "✅" : "❌")} |");
        sb.AppendLine($"| False edits | {a.FalseEditRate:P2} ({a.FalseEdits}/{a.CorrectWords}) | < 1% {(a.FalseEditRate < TargetFalse ? "✅" : "❌")} |");
        sb.AppendLine($"| Exact match | {a.ExactMatchRate:P0} | — |");
        sb.AppendLine($"| Model latency p50 / p95 | {Percentile(lat, 0.5)} ms / {Percentile(lat, 0.95)} ms | median < 2 s (5.5) |");
        sb.AppendLine();
        sb.AppendLine("## By noise level");
        sb.AppendLine();
        sb.AppendLine("| Level | Messages | Errors fixed | False edits | Exact |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var g in results.GroupBy(r => r.Item.Level).OrderBy(g => g.Key))
        {
            var x = Scorer.Combine(g.Select(r => r.Score));
            sb.AppendLine($"| {g.Key} | {x.Messages} | {x.ErrorsFixedRate:P1} | {x.FalseEditRate:P2} | {x.ExactMatchRate:P0} |");
        }
        sb.AppendLine();
        sb.AppendLine($"## Worst {worstN} (6.3.e)");
        sb.AppendLine();
        foreach (var r in results.OrderByDescending(r => (r.Score.NeedFix - r.Score.Fixed) + 3 * r.Score.FalseEdits).ThenBy(r => r.Item.Id).Take(worstN))
        {
            sb.AppendLine($"- **{r.Item.Id}** ({Category(r)}): fixed {r.Score.Fixed}/{r.Score.NeedFix}, false edits {r.Score.FalseEdits}");
            sb.AppendLine($"  - typed: `{r.Item.Noisy}`");
            sb.AppendLine($"  - result: `{r.Output}`");
            sb.AppendLine($"  - intended: `{r.Item.Clean}`");
        }
        return sb.ToString();
    }

    internal static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        double rank = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return Math.Round(sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo));
    }
}
