using GlobalAutocorrect.Core.Learning;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Learning;

/// <summary>D155 (user: "move as much as possible into the brain"): every judgement value is a brain knob, held to its range.</summary>
[Collection("Tunables")]
public sealed class TunablesTests
{
    [Fact]
    public void R4_6_d_D155_EveryValueStartsAtItsDefaultInsideItsRangeAndTheBrainKnowsIt()
    {
        Assert.NotEmpty(Tunables.All);
        foreach (var t in Tunables.All)
        {
            Assert.InRange(t.Default, t.Min, t.Max);
            Assert.True(Brain.TryRange(t.Name, out double min, out double max), t.Name);
            Assert.Equal((t.Min, t.Max), (min, max));
            Assert.False(string.IsNullOrWhiteSpace(t.Why), t.Name);
        }
    }

    [Fact]
    public void R4_6_d_D155_ABrainValueIsHeldToItsRangeAndALeftOutOneGoesBack()
    {
        try
        {
            var b = Brain.Parse("""{ "format": 1, "updated": "2026-11-20T12:00:00Z", "tuning": { "Enter.MaxHoldMs": 99999, "Place.Retries": 5 } }""")!;
            Tunables.Apply(b.Tuning);
            Assert.Equal(Tunables.EnterMaxHoldMs.Max, Tunables.EnterMaxHoldMs.Value);   // clamped, never 99 s
            Assert.Equal(5, Tunables.PlaceRetries.Int);
            Tunables.Apply(new Dictionary<string, double>());
            Assert.Equal(Tunables.PlaceRetries.Default, Tunables.PlaceRetries.Value);   // the brain dropped it: back to the default
        }
        finally { Tunables.Apply(null); }
    }

    [Fact]
    public void R4_6_d_D155_EveryKnobIsListedForWhoeverEditsTheBrain()
    {
        string readme = File.ReadAllText(Path.Combine(GlobalAutocorrect.Core.Tests.EvalData.EvalDataFiles.RepoRoot(), "brain", "README.md"));
        foreach (var t in Tunables.All) Assert.Contains($"`{t.Name}`", readme);
        foreach (var name in Brain.Knobs.Keys.Concat(Brain.RuleKnobs.Keys)) Assert.Contains($"`{name}`", readme);
    }
}
