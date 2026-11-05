using GlobalAutocorrect.Core.Prediction;
using GlobalAutocorrect.Core.Telemetry;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Telemetry;

/// <summary>D97, user request (2026-11-05): "this application should be automatically monitored, improving... within the
/// app itself". Where reading fields is slow, the app does less there, by itself, and goes back once reads are quick.</summary>
public sealed class ReadCostTests
{
    [Fact]
    public void R10_6_D97_AProgramWithSlowReadsGoesLightAndComesBackWhenTheyAreQuick()
    {
        var cost = new ReadCost();
        var changes = new List<(string, bool)>();
        cost.Changed += (app, light, _) => changes.Add((app, light));

        for (int i = 0; i < ReadCost.MinReads - 1; i++) cost.Record("msedge", 300);
        Assert.False(cost.IsLight("msedge"));                       // too few reads to judge
        cost.Record("msedge", 300);
        Assert.True(cost.IsLight("MSEDGE"));
        Assert.False(cost.IsLight("notepad"));
        Assert.Equal(["msedge"], cost.LightApps);

        for (int i = 0; i < ReadCost.Window; i++) cost.Record("msedge", 70);
        Assert.True(cost.IsLight("msedge"));                        // between the two marks: stays light (no flapping)
        for (int i = 0; i < ReadCost.Window; i++) cost.Record("msedge", 20);
        Assert.False(cost.IsLight("msedge"));
        Assert.Equal([("msedge", true), ("msedge", false)], changes);
    }

    [Fact]
    public void R10_6_D97_AFewSlowReadsAmongQuickOnesDontCount()
    {
        var cost = new ReadCost();
        for (int i = 0; i < ReadCost.Window; i++) cost.Record("claude", i % 20 == 0 ? 400 : 15);   // 2 slow in 40
        Assert.False(cost.IsLight("claude"));
    }

    [Theory]
    [InlineData(false, 90, true)]
    [InlineData(true, 90, false)]    // light: waits for a longer pause
    [InlineData(true, 300, true)]
    public void R7_1_D97_InALightProgramTheSuggestionWaitsForALongerPause(bool light, long sinceMs, bool reads) =>
        Assert.Equal(reads, FieldCatchUp.Paused(1, 1, sinceMs, light));
}
