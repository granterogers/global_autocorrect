using GlobalAutocorrect.Core.Ai;
using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Telemetry;
using GlobalAutocorrect.Core.Tests.Instant;

namespace GlobalAutocorrect.Core.Tests.Simulated;

/// <summary>Scripted model: maps the context it receives to a cleaned text, with a fixed latency.</summary>
public sealed class FakeLlm(Func<string, string> clean, double latencyMs = 300) : ILlmClient
{
    public List<string> Requests { get; } = [];
    public bool Hang { get; set; }

    public Task<LlmResult> CleanAsync(string text, CancellationToken cancellationToken)
    {
        Requests.Add(text);
        return Hang ? new TaskCompletionSource<LlmResult>().Task : Task.FromResult(new LlmResult(clean(text), latencyMs));
    }
}

public sealed class FakeSuggester(Dictionary<string, SpellSuggestion[]> table) : ISuggester
{
    public IReadOnlyList<SpellSuggestion> Lookup(string word, int max = 3) =>
        table.TryGetValue(word, out var s) ? s.Take(max).ToArray() : [];

    public static FakeSuggester Default() => new(new()
    {
        ["teh"] = [new("the", 1, 5_000_000), new("tea", 1, 20_000)],
        ["bookk"] = [new("book", 1, 90_000)],
        ["smiht"] = [new("smith", 2, 40_000)],
        ["wnet"] = [new("went", 1, 300_000), new("wet", 1, 300_000)],   // a genuine tie: not obvious (4.3.d)
    });
}

/// <summary>Engine + simulated field on a virtual clock. Typing advances time per key and ticks the engine.</summary>
public sealed class Harness
{
    public ManualClock Clock { get; } = new(10_000);
    public SimulatedEditor Ed { get; }
    public CorrectionEngine Engine { get; }
    public FakeLlm? Llm { get; }
    public HashSet<string> Personal { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Counters Counters { get; } = new();

    public Harness(Func<string, string>? model = null, double latencyMs = 300, EngineOptions? options = null)
    {
        Ed = new SimulatedEditor(Clock);
        if (model is not null) Llm = new FakeLlm(model, latencyMs);
        var deps = new EngineDeps(Clock, Ed, HunspellTests.Shared, FakeSuggester.Default(), Llm,
            Personal.Contains, Personal.Add, Counters);
        Engine = new CorrectionEngine(options ?? new EngineOptions(), deps);
        Engine.SetTarget(Ed);
    }

    public void Type(string s, int msPerKey = 80)
    {
        foreach (char c in s)
        {
            Clock.Advance(msPerKey);
            Engine.OnInput(Ed.TypeChar(c));
            Engine.Tick();
        }
    }

    public void Wait(int ms, int step = 25)
    {
        for (int t = 0; t < ms; t += step)
        {
            Clock.Advance(step);
            Engine.Tick();
        }
    }

    public string Text => Ed.Text;
}
