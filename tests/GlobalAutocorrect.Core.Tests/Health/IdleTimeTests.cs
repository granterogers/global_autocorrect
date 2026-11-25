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

    /// <summary>
    /// D182: input timestamps (GetLastInputInfo's dwTime, a key's time) are on GetTickCount's clock, which counts time
    /// asleep; Environment.TickCount doesn't. Compared with each other, a laptop that had slept 2.85 days never read as
    /// idle: quiet updates, deploy's wait and brain-eval's wait never came, and the hook was re-installed every tick.
    /// </summary>
    [Fact]
    public void R13_3_D182_NoInputTimestampIsComparedWithDotNetsTickCount()
    {
        string root = GlobalAutocorrect.Core.Tests.EvalData.EvalDataFiles.RepoRoot();
        var mixed = new[] { "src", "tests", "scripts" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.*", SearchOption.AllDirectories))
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".ps1", StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.line, @"Environment\.TickCount(?!64)")
                && System.Text.RegularExpressions.Regex.IsMatch(x.line, @"dwTime|\bk\.time\b"))
            .Select(x => $"{Path.GetRelativePath(root, x.f)}:{x.i + 1}")
            .ToList();
        Assert.Empty(mixed);
    }
}
