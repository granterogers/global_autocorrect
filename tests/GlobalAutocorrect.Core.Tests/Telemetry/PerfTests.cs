using GlobalAutocorrect.Core.Prediction;
using GlobalAutocorrect.Core.Telemetry;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Telemetry;

/// <summary>D94, user request (2026-11-05): "keep track of performance, like memory usage... sometimes it takes ages
/// for me to type letters".</summary>
public sealed class PerfTests
{
    [Theory]
    [InlineData(5, 5, 90, true)]     // nothing typed since, and long enough
    [InlineData(5, 5, 40, false)]    // too soon: still in a burst
    [InlineData(5, 6, 200, false)]   // another key came: that key predicts instead
    public void R7_1_D94_TheSuggestionReadsTheFieldOnlyOnceTypingPauses(long seq, long latest, long sinceMs, bool reads) =>
        Assert.Equal(reads, FieldCatchUp.Paused(seq, latest, sinceMs));

    [Fact]
    public void R8_6_D94_ThePerformanceLineHoldsNumbersOnlyAndSaysWhatWasSlow()
    {
        var t = new Timings();
        for (int i = 0; i < 100; i++) { t.Record("read", i < 90 ? 10 : 300); t.Record("key.hookDelay", 2); }
        var s = t.Snapshot();
        var a = new PerfSample(0, 200, 300, 0, 0, 0, 4500, 6000, 0, 0);
        var b = new PerfSample(60_000, 210, 310, 600, 5, 1, 4500, 5900, t.Total("read"), t.Total("key.hookDelay"));

        string line = PerfReport.Line(a, b, s["read"], s["key.hookDelay"]);
        Assert.StartsWith("perf: privateMB=210 ", line);
        Assert.Contains("readsPerMin=100 readsPerKey=1.00", line);
        Assert.EndsWith("SLOW reads-slow", line);        // 1 read in 10 took 300 ms
        Assert.DoesNotContain("keys-late", line);        // keys reached the app in 2 ms

        var idle = new PerfSample(120_000, 210, 310, 600, 5, 1, 4500, 5900, b.Reads, b.Keys);
        Assert.EndsWith(" ok", PerfReport.Line(b, idle, s["read"], s["key.hookDelay"]));   // old slow reads don't count again
    }

    [Fact]
    public void R8_6_D94_TotalsCountPastTheKeptSamples()
    {
        var t = new Timings();
        for (int i = 0; i < Timings.Keep + 5; i++) t.Record("read", 1);
        Assert.Equal(Timings.Keep + 5, t.Total("read"));
        Assert.Equal(Timings.Keep, t.Snapshot()["read"].Count);
    }
}
