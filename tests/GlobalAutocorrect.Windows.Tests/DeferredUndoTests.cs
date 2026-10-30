using GlobalAutocorrect.Core.Undo;
using GlobalAutocorrect.Windows.Input;
using Xunit;

namespace GlobalAutocorrect.Windows.Tests;

public sealed class DeferredUndoTests
{
    private bool _down = true;
    private long _now;
    private readonly List<bool> _ran = [];

    private UndoResult Undo(bool global) { _ran.Add(global); return UndoResult.Reverted; }

    [Fact]
    public void R4_8_a_UndoWaitsUntilCtrlIsReleased()
    {
        // Found by E2E: an undo sent while Ctrl was still down deleted the correction and lost the retyped word.
        var d = new DeferredUndo(() => _down, () => _now);
        d.Request(global: false);
        _now = 50; d.Run(Undo);
        Assert.Empty(_ran);
        _down = false; _now = 60; d.Run(Undo);
        Assert.Equal([false], _ran);
        Assert.False(d.Pending);
        d.Run(Undo);
        Assert.Single(_ran);   // once only
    }

    [Fact]
    public void R4_8_b_TheShortcutUndoReportsItsResultAfterRelease()
    {
        var d = new DeferredUndo(() => _down, () => _now);
        UndoResult? got = null;
        d.Request(global: true, r => got = r);
        d.Run(Undo);
        Assert.Null(got);
        _down = false; d.Run(Undo);
        Assert.Equal(UndoResult.Reverted, got);
        Assert.Equal([true], _ran);
    }

    [Fact]
    public void R4_8_a_AKeyHeldForeverDoesNotBlockTheUndo()
    {
        // A modifier the hook never saw released (the key-up went to the secure desktop) must not stall undo for good.
        var d = new DeferredUndo(() => _down, () => _now, maxWaitMs: 1000);
        d.Request(global: false);
        _now = 999; d.Run(Undo);
        Assert.Empty(_ran);
        _now = 1000; d.Run(Undo);
        Assert.Single(_ran);
    }
}
