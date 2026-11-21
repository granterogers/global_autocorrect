using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GlobalAutocorrect.Core.Ai;
using GlobalAutocorrect.Core.Prediction;
using Xunit;

namespace GlobalAutocorrect.Llm.Tests;

/// <summary>
/// D159 benchmark (model tier, on request: `dotnet test --filter Category=Bench`): suggestions from the word list alone
/// against the model's reading of the sentence, on tuning-set sentences (never the holdout, 6.1), and how fast the model
/// answers on its second slot, with and without the clean-up running on the first. Writes artifacts/suggest-bench.md.
/// </summary>
[Trait("Category", "Model")]
[Trait("Category", "Bench")]
public sealed class SuggestBenchTests
{
    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "GlobalAutocorrect.slnx"))) d = d.Parent;
        return d!.FullName;
    }

    private static double Pct(List<double> v, double q) { if (v.Count == 0) return 0; v.Sort(); return v[Math.Min(v.Count - 1, (int)(v.Count * q))]; }

    [Fact]
    public async Task R7_1_D159_SuggestionsFromTheModelAgainstTheWordList()
    {
        var ct = TestContext.Current.CancellationToken;
        string repo = Repo();
        string build = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "scripts", "manifest.json"))).RootElement.GetProperty("llama").GetProperty("build").GetString()!;
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlobalAutocorrect", "cache");
        string exe = Path.Combine(cache, "llama", build, "cuda", "llama-server.exe"), modelPath = Path.Combine(cache, "models", "gemma-4-E4B-it-Q5_K_M.gguf");
        if (!File.Exists(exe) || !File.Exists(modelPath)) Assert.Skip("Model not downloaded");
        await using var host = new LlamaServerHost(new ServerOptions(exe, modelPath));
        Assert.True(await host.StartAsync(ct), "server did not start");
        using var client = new LlamaServerClient(host.BaseAddress!, PromptTemplate.Default, apiKey: host.ApiKey);

        var ngram = NgramModel.FromAssets(Path.Combine(repo, "assets"));
        string histPath = Path.Combine(Path.GetTempPath(), "ga-bench-" + Guid.NewGuid().ToString("N") + ".txt");
        var nextMs = new List<double>();
        IReadOnlyList<TokenChance>? Next(string prompt)
        {
            var sw = Stopwatch.StartNew();
            var r = client.NextAsync(prompt, 100, ct).GetAwaiter().GetResult();
            nextMs.Add(sw.Elapsed.TotalMilliseconds);
            return r;
        }
        var plain = new Predictor(ngram, new UserHistory(histPath));
        var withModel = new Predictor(ngram, new UserHistory(histPath)) { ModelNext = Next };

        await client.CleanAsync("warm up teh cache", ct);
        Next("Warm up the second");

        // Words to finish: from clean tuning sentences, a word of 4+ letters after the first, two or three letters typed.
        var rnd = new Random(159);
        var cases = new List<(string Before, string Word)>();
        foreach (var line in File.ReadLines(Path.Combine(repo, "data", "eval", "tuning.jsonl")))
        {
            string clean = JsonDocument.Parse(line).RootElement.GetProperty("clean").GetString()!;
            var words = clean.Split(' ');
            for (int tries = 0; tries < 2; tries++)
            {
                int i = rnd.Next(1, Math.Max(2, words.Length));
                if (i >= words.Length) continue;
                string w = new string(words[i].TakeWhile(char.IsLetter).ToArray());
                if (w.Length < 4 || w.Length != words[i].Length && !char.IsPunctuation(words[i][w.Length])) continue;
                int typed = rnd.Next(2, 4);
                cases.Add((string.Join(' ', words[..i]) + " " + w[..typed], w.ToLowerInvariant()));
            }
            if (cases.Count >= 300) break;
        }

        int n = cases.Count;
        int[] offered = new int[2], right = new int[2];
        // D160: every pick the model makes, with its chance, to see what each cut-off would give.
        var picks = new List<(double Chance, bool Right, bool ListRight, bool ListOffered)>();
        GlobalAutocorrect.Core.Learning.Tunables.Apply(new Dictionary<string, double> { ["Predict.ModelMinChance"] = 0, ["Predict.ModelLead"] = 1 });
        var sweep = new Predictor(ngram, new UserHistory(histPath)) { ModelNext = Next };
        foreach (var (before, word) in cases)
        {
            var a = plain.Suggest(before);
            if (a is not null) { offered[0]++; if (a.Word.Equals(word, StringComparison.OrdinalIgnoreCase)) right[0]++; }
            var s = sweep.Suggest(before);
            if (s is not null)
                picks.Add((sweep.LastModelChance, s.Word.Equals(word, StringComparison.OrdinalIgnoreCase), a?.Word.Equals(word, StringComparison.OrdinalIgnoreCase) == true, a is not null));
        }
        GlobalAutocorrect.Core.Learning.Tunables.Apply(null);
        foreach (var (before, word) in cases)
        {
            var b = withModel.Suggest(before);
            if (b is not null) { offered[1]++; if (b.Word.Equals(word, StringComparison.OrdinalIgnoreCase)) right[1]++; }
        }

        // D163/D164: completions at several leads, and next words (nothing typed yet) at several chances.
        string Sweep(string title, IEnumerable<(string Name, Dictionary<string, double> Tuning)> settings, List<(string Before, string Word)> set, bool includePlain)
        {
            var sb = new StringBuilder().AppendLine($"| {title} | offered | right | wrong | right when offered |").AppendLine("|---|---|---|---|---|");
            var runs = settings.ToList();
            if (includePlain) runs.Insert(0, ("word list", new Dictionary<string, double> { ["Predict.UseModel"] = 0 }));
            foreach (var (name, tuning) in runs)
            {
                GlobalAutocorrect.Core.Learning.Tunables.Apply(tuning);
                var pr = new Predictor(ngram, new UserHistory(histPath)) { ModelNext = Next };
                int off = 0, ok = 0;
                foreach (var (before, word) in set)
                {
                    var sug = pr.Suggest(before);
                    if (sug is null) continue;
                    off++;
                    if (sug.Word.Equals(word, StringComparison.OrdinalIgnoreCase)) ok++;
                }
                sb.AppendLine($"| {name} | {off} ({100.0 * off / set.Count:F0}%) | {ok} ({100.0 * ok / set.Count:F0}%) | {off - ok} ({100.0 * (off - ok) / set.Count:F0}%) | {(off > 0 ? 100.0 * ok / off : 0):F0}% |");
            }
            GlobalAutocorrect.Core.Learning.Tunables.Apply(null);
            return sb.ToString();
        }
        string leadTable = Sweep("completion, model lead", new[] { 1.5, 2, 3, 5 }.Select(l => ($"lead {l}", new Dictionary<string, double> { ["Predict.ModelLead"] = l })), cases, includePlain: false);
        var nextCases = new List<(string Before, string Word)>();
        var rnd2 = new Random(163);
        foreach (var line in File.ReadLines(Path.Combine(repo, "data", "eval", "tuning.jsonl")))
        {
            var words = JsonDocument.Parse(line).RootElement.GetProperty("clean").GetString()!.Split(' ');
            if (words.Length < 4) continue;
            int i = rnd2.Next(2, words.Length);
            string w = new string(words[i].TakeWhile(char.IsLetter).ToArray());
            if (w.Length < 2 || !char.IsLetter(words[i - 1][^1])) continue;
            nextCases.Add((string.Join(' ', words[..i]) + " ", w.ToLowerInvariant()));
            if (nextCases.Count >= 200) break;
        }
        string nextTable = Sweep($"next word ({nextCases.Count} places)", new[] { 0.3, 0.5, 0.7 }.Select(c => ($"model, chance ≥ {c}", new Dictionary<string, double> { ["Predict.ModelNextMinChance"] = c })), nextCases, includePlain: true);

        // The clean-up's speed alone, and with suggestions asked on the other slot at the same time.
        var cleanAlone = new List<double>(); var cleanBusy = new List<double>();
        for (int i = 0; i < 12; i++) cleanAlone.Add((await client.CleanAsync($"Message {i}: i wnet to the shops and forgot teh milk again.", ct)).LatencyMs);
        for (int i = 0; i < 12; i++)
        {
            var suggest = Task.Run(() => { for (int k = 0; k < 4; k++) Next($"I will send the report to the team on Mon"); }, ct);
            cleanBusy.Add((await client.CleanAsync($"Message {i}: i wnet to the shops and forgot teh milk again.", ct)).LatencyMs);
            await suggest;
        }

        string Row(string name, int i) => $"| {name} | {offered[i]} ({100.0 * offered[i] / n:F0}%) | {right[i]} ({100.0 * right[i] / n:F0}%) | {offered[i] - right[i]} ({100.0 * (offered[i] - right[i]) / n:F0}%) | {(offered[i] > 0 ? 100.0 * right[i] / offered[i] : 0):F0}% |";
        var report = new StringBuilder()
            .AppendLine($"# Suggestions: word list against the model (D159), {n} words, tuning set")
            .AppendLine()
            .AppendLine("| | offered | right | wrong | right when offered |")
            .AppendLine("|---|---|---|---|---|")
            .AppendLine(Row("word list", 0))
            .AppendLine(Row("model", 1))
            .AppendLine()
            .AppendLine("| model pick at chance ≥ | offered | right | wrong | right when offered |")
            .AppendLine("|---|---|---|---|---|")
            .AppendLine(string.Concat(new[] { 0.15, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9 }.Select(c =>
            {
                var at = picks.Where(p => p.Chance >= c).ToList();
                int r = at.Count(p => p.Right);
                return $"| {c:F2} | {at.Count} ({100.0 * at.Count / n:F0}%) | {r} ({100.0 * r / n:F0}%) | {at.Count - r} ({100.0 * (at.Count - r) / n:F0}%) | {(at.Count > 0 ? 100.0 * r / at.Count : 0):F0}% |\n";
            })))
            .AppendLine(leadTable)
            .AppendLine(nextTable)
            .AppendLine($"Model answer for a suggestion (slot 1): p50 {Pct(nextMs, 0.5):F0} ms, p90 {Pct(nextMs, 0.9):F0} ms, p99 {Pct(nextMs, 0.99):F0} ms, {nextMs.Count} calls")
            .AppendLine($"Clean-up alone: p50 {Pct(cleanAlone, 0.5):F0} ms, p90 {Pct(cleanAlone, 0.9):F0} ms")
            .AppendLine($"Clean-up with suggestions asked meanwhile (D160: skipped while it runs): p50 {Pct(cleanBusy, 0.5):F0} ms, p90 {Pct(cleanBusy, 0.9):F0} ms")
            .ToString();
        Directory.CreateDirectory(Path.Combine(repo, "artifacts"));
        File.WriteAllText(Path.Combine(repo, "artifacts", "suggest-bench.md"), report);
        try { File.Delete(histPath); } catch (IOException) { }
        TestContext.Current.SendDiagnosticMessage(report);
    }
}
