using Xunit;

namespace GlobalAutocorrect.E2E;

/// <summary>
/// D183: the real-typing test's model stand-in must never "fix" its own correction. Stress runs 67 and 69 failed in every
/// editor because one seed picked "aor"→"or" and "or"→"for": the stand-in turned the fixed "or" into "for". No screen
/// needed: this only picks the messages.
/// </summary>
public sealed class StandInTests
{
    [Fact]
    public void R4_1_D183_NoCorrectionIsAnotherPickedMessagesTypo()
    {
        for (int seed = 0; seed <= 400; seed++)
        {
            var fixes = RealTypingTests.FixesFor(seed);
            var chained = fixes.Where(f => fixes.ContainsKey(f.Value)).Select(f => $"{f.Key}→{f.Value}→{fixes[f.Value]}").ToList();
            Assert.True(chained.Count == 0, $"seed {seed}: {string.Join(", ", chained)}");
        }
    }
}
