using GlobalAutocorrect.Core.Health;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Health;

/// <summary>D153: the install of v1.43.0 began "after 69,443 minutes idle" while the user was at the keyboard.</summary>
public sealed class IdleTimeTests
{
    [Fact]
    public void R13_3_D153_TenMinutesIdleReadsAsTenMinutes() =>
        Assert.Equal(TimeSpan.FromMinutes(10), IdleTime.Since(1_000_000, 1_600_000));

    [Fact]
    public void R13_3_D153_InputStampedJustAfterTheClockReadingIsNotDaysIdle() =>
        Assert.Equal(TimeSpan.Zero, IdleTime.Since(1_000_005, 1_000_000));

    [Fact]
    public void R13_3_D153_TheTickCountWrappingStillCountsForward() =>
        Assert.Equal(TimeSpan.FromMilliseconds(151), IdleTime.Since(uint.MaxValue - 50, 100));
}
