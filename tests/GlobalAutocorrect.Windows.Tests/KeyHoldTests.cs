using GlobalAutocorrect.Windows.Input;
using GlobalAutocorrect.Windows.Native;
using Xunit;

namespace GlobalAutocorrect.Windows.Tests;

/// <summary>D122: the user's keys wait while the caret is on an earlier line, and come back in order, none lost.</summary>
[Collection("KeyHold")]
public sealed class KeyHoldTests
{
    [Fact]
    public void R4_6_e_D122_HeldKeysAreReplayedInOrderAndNothingIsHeldOtherwise()
    {
        var sent = new List<INPUT>();
        var old = KeyHold.Sender;
        KeyHold.Sender = batch => { sent.AddRange(batch); return true; };
        try
        {
            Assert.False(KeyHold.TryHold(0x41, 0x1E, 0, down: true));   // no write in progress: the key goes straight on
            KeyHold.Begin();
            Assert.True(KeyHold.TryHold(0x41, 0x1E, 0, down: true));
            Assert.True(KeyHold.TryHold(0x41, 0x1E, 0, down: false));
            Assert.True(KeyHold.TryHold(0x25, 0x4B, 0x01, down: true));   // Left, extended
            Assert.Equal(3, KeyHold.Release());
            Assert.False(KeyHold.Active);
            Assert.Equal([0x41, 0x41, 0x25], sent.Select(i => (int)i.U.ki.wVk));
            Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, sent[1].U.ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP);
            Assert.Equal(NativeMethods.KEYEVENTF_EXTENDEDKEY, sent[2].U.ki.dwFlags & NativeMethods.KEYEVENTF_EXTENDEDKEY);
            Assert.All(sent, i => Assert.Equal(KeyHold.ReplayTag, i.U.ki.dwExtraInfo));
            Assert.False(KeyHold.TryHold(0x41, 0x1E, 0, down: true));
        }
        finally { KeyHold.Release(); KeyHold.Sender = old; }
    }

    [Fact]
    public void R4_6_e_D122_TheHoldLetsGoOnItsOwn()
    {
        var old = KeyHold.Sender;
        KeyHold.Sender = _ => true;
        try
        {
            KeyHold.Begin();
            Assert.True(KeyHold.TryHold(0x41, 0x1E, 0, down: true));
            Thread.Sleep(KeyHold.MaxHoldMs + 500);
            Assert.False(KeyHold.Active);   // never a dead keyboard
        }
        finally { KeyHold.Release(); KeyHold.Sender = old; }
    }
}
