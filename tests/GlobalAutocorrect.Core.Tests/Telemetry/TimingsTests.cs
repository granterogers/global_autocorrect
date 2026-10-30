using GlobalAutocorrect.Core.Telemetry;
using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Telemetry;

public class TimingsTests
{
    [Fact]
    public void R12_1_D64_TypicalAndSlowestFivePercentAreReported()
    {
        var t = new Timings();
        for (int i = 1; i <= 100; i++) t.Record("ai.keyToFix", i);
        var s = t.Snapshot()["ai.keyToFix"];
        Assert.Equal(100, s.Count);
        Assert.Equal(50, s.P50);
        Assert.Equal(95, s.P95);
        Assert.Equal(100, s.Max);
    }

    [Fact]
    public void R12_1_D64_OnlyTheLatestSamplesAreKept()
    {
        var t = new Timings();
        for (int i = 0; i < Timings.Keep + 500; i++) t.Record("read", i < 500 ? 10_000 : 5);
        Assert.Equal(5, t.Snapshot()["read"].Max);   // the old slow ones have gone
    }

    [Fact]
    public void R12_1_D64_TheWaitForAFixIsMeasuredFromTheKeyThatEndedTheWord()
    {
        // "we saw teh" then space: the instant fix lands after the 60 ms quiet rule (D47), measured from the space.
        var h = new Harness();
        h.Type("we saw teh ");
        var s = h.Counters.Timings.Snapshot();
        Assert.True(s.ContainsKey("instant.keyToFix"));
        Assert.InRange(s["instant.keyToFix"].P50, 60, 150);
    }

    [Fact]
    public void R12_1_D64_TheWaitForAnAiFixIsMeasuredFromTheSentencesLastKey()
    {
        var h = new Harness(model: s => s.Replace("went their", "went there"), latencyMs: 300);
        h.Type("we went their yesterday. ");
        h.Wait(1000);
        var s = h.Counters.Timings.Snapshot();
        Assert.Equal(300, s["ai.model"].P50);
        Assert.InRange(s["ai.keyToFix"].P50, 300, 600);   // the model's 300 ms plus the wait for a gap and the write
    }
}
