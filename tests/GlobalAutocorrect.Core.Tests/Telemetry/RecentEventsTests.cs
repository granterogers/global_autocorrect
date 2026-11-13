using GlobalAutocorrect.Core.Telemetry;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Telemetry;

public sealed class RecentEventsTests
{
    [Fact]
    public void D126_KeepsOnlyTheLastMinuteAndAHalfInOrder()
    {
        long now = 0;
        var r = new RecentEvents(() => now);
        r.Add("first");
        now = 60_000;
        r.Add("second");
        now = 100_000;   // "first" is now 100 s old
        r.Add("third");
        var lines = r.Snapshot();
        Assert.Equal(2, lines.Count);
        Assert.EndsWith("second", lines[0]);
        Assert.EndsWith("third", lines[1]);
    }

    [Fact]
    public void D126_NeverGrowsPastItsLimit()
    {
        var r = new RecentEvents(() => 0, maxLines: 10);
        for (int i = 0; i < 50; i++) r.Add($"line {i}");
        Assert.Equal(10, r.Snapshot().Count);
        Assert.EndsWith("line 49", r.Snapshot()[^1]);
    }
}
