using GlobalAutocorrect.Core.Ai;
using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Eval;
using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Simulation;
using GlobalAutocorrect.Core.Telemetry;
using GlobalAutocorrect.Core.Text;

namespace GlobalAutocorrect.Eval;

/// <summary>Remembers the last request so the runner can hold virtual time while the real model works.</summary>
internal sealed class RecordingLlm(ILlmClient inner, List<double> latencies, Action<string>? trace = null) : ILlmClient
{
    public Task<LlmResult>? Last { get; private set; }

    public Task<LlmResult> CleanAsync(string text, CancellationToken cancellationToken)
    {
        Last = Record(text, inner.CleanAsync(text, cancellationToken));
        return Last;
    }

    private async Task<LlmResult> Record(string text, Task<LlmResult> t)
    {
        var r = await t.ConfigureAwait(false);
        lock (latencies) latencies.Add(r.LatencyMs);
        if (trace is not null)
        {
            // Eval data only: show what the rules make of the model's reply (tuning, T6.2).
            string cleaned = AiTrigger.AlignToContext(text, r.Text);
            var verdict = Rules!.Filter(text, cleaned, WordDiff.Compute(text, cleaned));
            trace($"  ctx:   {text}\n  model: {r.Text}\n  whole: {verdict.WholeRejection}");
            foreach (var v in verdict.Verdicts)
                trace($"    {(v.Accepted ? "accept" : "REJECT")} {v.Edit.Kind,-14} {string.Join(' ', v.Edit.OriginalWords)} -> {string.Join(' ', v.Edit.NewWords)} {(v.Accepted ? "" : v.Reason.ToString())}");
        }
        return r;
    }

    public EditRules? Rules { get; init; }
}

internal sealed record EvalItem(string Id, string Clean, string Noisy, string Level, IReadOnlyList<string> Ops);

internal sealed record ItemResult(EvalItem Item, string Output, MessageScore Score, string Counters = "");

/// <summary>
/// Scores the whole pipeline (6.2): each noisy message is typed into a simulated field at a realistic pace through the
/// real engine (instant layer, AI layer, edit rules, timing rules). Virtual time stands still while the real model is
/// working, then the result lands after its real latency, while "typing" carries on, exactly as it would live.
/// </summary>
internal sealed class PipelineRunner(ISpellChecker speller, ISuggester suggester, ILlmClient llm, EngineOptions options, int seed = 20261029, Func<string, string, long>? pairCount = null, Func<string, long>? wordCount = null)
{
    /// <summary>Real model round-trip times for every request made (6.3.d).</summary>
    public List<double> Latencies { get; } = [];

    /// <summary>Ids whose model traffic and rule verdicts are printed (eval data only).</summary>
    public HashSet<string> TraceIds { get; init; } = [];

    /// <summary>
    /// D42: the field behaves like a real editor: blank lines and double spaces reported differently, Edge-style
    /// suggestions after the caret on some reads, reads that lag the last keys, and the app's own autocorrect on common
    /// typos (Notepad, Word, Outlook do this). Each of these broke real use; with them on, the score is the real one.
    /// </summary>
    public bool Realistic => Quirks.Count > 0;

    /// <summary>Which behaviours to switch on: blank, nbsp, ghost, lag, host ("all" for every one).</summary>
    public HashSet<string> Quirks { get; init; } = [];

    private bool On(string q) => Quirks.Contains("all") || Quirks.Contains(q);

    private static readonly Dictionary<string, string> HostFixes = new(StringComparer.Ordinal)
    {
        ["teh"] = "the", ["dont"] = "don't", ["im"] = "I'm", ["thier"] = "their", ["recieve"] = "receive", ["wich"] = "which",
        ["becuase"] = "because", ["adn"] = "and", ["taht"] = "that", ["i"] = "I",
    };

    public async Task<ItemResult> RunAsync(EvalItem item, CancellationToken ct)
    {
        var clock = new ManualClock(1_000_000);
        var rng = new Random(HashCode.Combine(seed, item.Id));
        var ed = new SimulatedEditor(clock, item.Id);
        if (Realistic)
        {
            ed.ReportsBlankLinesOnce = On("blank");
            ed.ReportsDoubleSpaceAsNbsp = On("nbsp");
            ed.GhostAfterCaret = " and more";
            ed.Chance = new Random(HashCode.Combine(seed, item.Id, 7));
            ed.GhostChance = On("ghost") ? 0.2 : 0;
            ed.LagChance = On("lag") ? 0.1 : 0;
            if (On("host")) ed.HostAutocorrect = w => HostFixes.GetValueOrDefault(w);
        }
        var rec = new RecordingLlm(llm, Latencies, TraceIds.Contains(item.Id) ? Console.WriteLine : null)
        {
            Rules = new EditRules(options.Rules, speller, _ => false),
        };
        var personal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counters = new Counters();
        var engine = new CorrectionEngine(options, new EngineDeps(clock, ed, speller, suggester, rec, personal.Contains, personal.Add, counters, PairCount: pairCount, WordCount: wordCount));
        engine.SetTarget(ed);

        foreach (char c in item.Noisy)
        {
            await HoldWhileModelWorks(engine, rec, ct);
            // 60–120 ms between keys: a fast typist. The app ticks every 25 ms meanwhile (D47).
            for (int gap = rng.Next(60, 121); gap > 0; gap -= 25) { clock.Advance(Math.Min(25, gap)); if (gap > 25) engine.Tick(); }
            engine.OnInput(ed.TypeChar(c));
            engine.Tick();
        }
        // The user stops: let pauses, AI passes and queued writes play out.
        for (int t = 0; t < 8000; t += 25)   // long enough for the look-back after a pause (D49)
        {
            await HoldWhileModelWorks(engine, rec, ct);
            clock.Advance(25);
            engine.Tick();
        }
        if (TraceIds.Contains(item.Id)) Console.WriteLine($"  counters: {counters.ToJson()} lastBreak={engine.Session.LastBreak}");
        return new ItemResult(item, ed.Text, Scorer.Score(item.Clean, item.Noisy, ed.Text), counters.ToJson());
    }

    private static async Task HoldWhileModelWorks(CorrectionEngine engine, RecordingLlm rec, CancellationToken ct)
    {
        if (engine.AiInFlight && rec.Last is { IsCompleted: false } t)
        {
            try { await t.WaitAsync(ct); } catch (Exception e) when (e is not OperationCanceledException) { }
        }
    }
}
