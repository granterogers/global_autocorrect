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

internal sealed record ItemResult(EvalItem Item, string Output, MessageScore Score);

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

    public async Task<ItemResult> RunAsync(EvalItem item, CancellationToken ct)
    {
        var clock = new ManualClock(1_000_000);
        var ed = new SimulatedEditor(clock, item.Id);
        var rec = new RecordingLlm(llm, Latencies, TraceIds.Contains(item.Id) ? Console.WriteLine : null)
        {
            Rules = new EditRules(options.Rules, speller, _ => false),
        };
        var personal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var engine = new CorrectionEngine(options, new EngineDeps(clock, ed, speller, suggester, rec, personal.Contains, personal.Add, new Counters(), PairCount: pairCount, WordCount: wordCount));
        engine.SetTarget(ed);
        var rng = new Random(HashCode.Combine(seed, item.Id));

        foreach (char c in item.Noisy)
        {
            await HoldWhileModelWorks(engine, rec, ct);
            clock.Advance(rng.Next(60, 121));   // 60–120 ms between keys: a fast typist
            engine.OnInput(ed.TypeChar(c));
            engine.Tick();
        }
        // The user stops: let pauses, AI passes and queued writes play out.
        for (int t = 0; t < 4000; t += 25)
        {
            await HoldWhileModelWorks(engine, rec, ct);
            clock.Advance(25);
            engine.Tick();
        }
        return new ItemResult(item, ed.Text, Scorer.Score(item.Clean, item.Noisy, ed.Text));
    }

    private static async Task HoldWhileModelWorks(CorrectionEngine engine, RecordingLlm rec, CancellationToken ct)
    {
        if (engine.AiInFlight && rec.Last is { IsCompleted: false } t)
        {
            try { await t.WaitAsync(ct); } catch (Exception e) when (e is not OperationCanceledException) { }
        }
    }
}
