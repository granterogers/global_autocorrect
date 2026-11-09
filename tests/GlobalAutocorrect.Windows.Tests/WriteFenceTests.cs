using GlobalAutocorrect.Windows.Input;
using Xunit;

namespace GlobalAutocorrect.Windows.Tests;

/// <summary>D105: an edit batch reaches the app only if no key got there after the gate's last look.</summary>
[Collection("WriteFence")]
public sealed class WriteFenceTests
{
    [Fact]
    public void R4_6_c_D105_ABatchGoesThroughWhenNothingArrivedSinceTheGateLooked()
    {
        WriteFence.Reset();
        nuint tag = WriteFence.Arm(expectedKeySeq: 41);
        Assert.True(WriteFence.IsFenced(tag));
        Assert.False(WriteFence.IsFenced(InjectionTag.Value));
        Assert.Null(WriteFence.Blocked(tag));                    // the hook hasn't seen it yet
        Assert.False(WriteFence.Swallow(tag, 41, modifierDown: false));
        Assert.False(WriteFence.Swallow(tag, 42, modifierDown: true));   // later events of the batch: same verdict
        Assert.False(WriteFence.Blocked(tag));
    }

    [Theory]
    [InlineData(42, false)]   // the user's key got to the app first ("oe has" → "oerhas")
    [InlineData(41, true)]    // Shift went down first ("os" → "SO", a line selected and deleted)
    public void R4_6_c_D105_ABatchIsSwallowedWholeWhenAKeyOrShiftGotThereFirst(long seqAtHook, bool shift)
    {
        WriteFence.Reset();
        nuint tag = WriteFence.Arm(expectedKeySeq: 41);
        Assert.True(WriteFence.Swallow(tag, seqAtHook, shift));
        Assert.True(WriteFence.Swallow(tag, 41, modifierDown: false));   // every later event of it too
        Assert.True(WriteFence.Blocked(tag));
    }

    [Fact]
    public void R4_6_c_D105_AnOlderBatchIsNeverLetThrough()
    {
        WriteFence.Reset();
        nuint old = WriteFence.Arm(10);
        nuint now = WriteFence.Arm(10);
        Assert.True(WriteFence.Swallow(old, 10, false));
        Assert.False(WriteFence.Swallow(now, 10, false));
    }
}
