using GlobalAutocorrect.Windows.Input;
using GlobalAutocorrect.Windows.Native;
using Xunit;
using static GlobalAutocorrect.Windows.Native.NativeMethods;

namespace GlobalAutocorrect.Windows.Tests;

/// <summary>D154: Enter held in a field that sends on Enter, while a fix for the line is on its way.</summary>
[Collection("EnterGate")]
public sealed class EnterGateTests : IDisposable
{
    private readonly List<INPUT[]> _sent = [];
    private nint _window = 42;

    public EnterGateTests()
    {
        EnterGate.Reset();
        EnterGate.Send = i => { lock (_sent) _sent.Add(i); return true; };
        EnterGate.Foreground = () => _window;
    }

    public void Dispose()
    {
        EnterGate.Reset();
        EnterGate.Send = GlobalAutocorrect.Windows.Write.SendInputWriter.Send;
        EnterGate.Foreground = GetForegroundWindow;
    }

    private static bool Key(uint vk, bool down, bool modifier = false) => EnterGate.TryHold(vk, 0, 0, down, modifier);

    [Fact]
    public void R4_6_D154_EnterAndTheKeysAfterItWaitThenGoInOrder()
    {
        EnterGate.Armed = true;
        Assert.True(Key(VK_RETURN, true));
        Assert.True(EnterGate.TakeStarted());
        Assert.True(Key(VK_RETURN, false));
        Assert.True(Key('H', true));   // the next message begun meanwhile
        var released = System.Diagnostics.Stopwatch.StartNew();
        EnterGate.Release("fixes in");
        // D161: Enter goes on its own (the page sends the message or makes the paragraph a moment later) ...
        var first = Assert.Single(_sent);
        Assert.Equal([(ushort)VK_RETURN, (ushort)VK_RETURN], first.Select(i => i.U.ki.wVk));
        Assert.All(first, i => Assert.Equal(EnterGate.ReplayTag, i.U.ki.dwExtraInfo));
        // ... and the keys typed after it follow after a gap, in order, with any typed meanwhile (D156).
        Assert.True(Key('I', true));
        for (int i = 0; i < 100 && _sent.Count < 2; i++) Thread.Sleep(10);
        Assert.True(released.ElapsedMilliseconds >= Core.Learning.Tunables.EnterGapMs.Int - 5, $"{released.ElapsedMilliseconds} ms");
        Assert.Equal([(ushort)'H', (ushort)'I'], _sent[1].Select(i => i.U.ki.wVk));
        for (int i = 0; i < 100 && EnterGate.Holding; i++) Thread.Sleep(10);
        Assert.False(EnterGate.Holding);   // nothing more came: done, without waiting to see the replay come back
        Assert.False(Key('J', true));
    }

    [Theory]
    [InlineData(false, false)]   // not a field that sends on Enter
    [InlineData(true, true)]     // Shift+Enter: a line break, never a send
    public void R4_6_D154_EnterIsLeftAloneOtherwise(bool armed, bool modifier)
    {
        EnterGate.Armed = armed;
        Assert.False(Key(VK_RETURN, true, modifier));
        Assert.False(Key('A', true));
        Assert.False(EnterGate.Holding);
    }

    [Fact]
    public void R4_6_D154_HeldKeysAreNeverSentToAnotherWindow()
    {
        EnterGate.Armed = true;
        Key(VK_RETURN, true);
        _window = 99;   // the user clicked into another program during the hold
        EnterGate.Release("fixes in");
        Assert.Empty(_sent);
        Assert.False(EnterGate.Holding);
    }

    [Fact]
    public void R4_6_D154_TheTimerNeverLetsKeysGoInTheMiddleOfAWrite()
    {
        // Stress, chat: released while a fix was being placed, the keys typed over the selected word ("o dnpelaera").
        bool writing = true;
        EnterGate.WriteInProgress = () => writing;
        try
        {
            EnterGate.Armed = true;
            Key(VK_RETURN, true);
            Thread.Sleep(EnterGate.MaxHoldMs + 400);
            Assert.Empty(_sent);       // still writing: held
            writing = false;
            for (int i = 0; i < 50 && _sent.Count == 0; i++) Thread.Sleep(10);
            Assert.Single(_sent);      // the write is done: Enter goes
        }
        finally { EnterGate.WriteInProgress = () => KeyHold.Active; }
    }

    [Fact]
    public void R4_6_D166_TheHoldKeepsTimeWhenTheThreadPoolIsBusy()
    {
        // Stress: suggestions waiting on the model had taken up the thread pool, and keys after a released Enter came out
        // seconds late. The gate keeps its own clock.
        var release = new ManualResetEventSlim(false);   // not disposed: the blocked items may still wait on it after the test
        ThreadPool.GetMinThreads(out int min, out _);
        for (int i = 0; i < min * 4 + 8; i++) ThreadPool.UnsafeQueueUserWorkItem(_ => { try { release.Wait(3000); } catch (ObjectDisposedException) { } }, null);
        try
        {
            EnterGate.Armed = true;
            EnterGate.HoldMs = 200;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Key(VK_RETURN, true);
            for (int i = 0; i < 100 && _sent.Count == 0; i++) Thread.Sleep(10);
            Assert.Single(_sent);
            Assert.True(sw.ElapsedMilliseconds < 600, $"{sw.ElapsedMilliseconds} ms");
        }
        finally { release.Set(); }
    }

    [Fact]
    public void R4_6_D156_AnInstantFixHoldIsShort()
    {
        EnterGate.Armed = true;
        EnterGate.HoldMs = 200;   // waiting only for the word just finished
        Key(VK_RETURN, true);
        for (int i = 0; i < 60 && _sent.Count == 0; i++) Thread.Sleep(10);
        Assert.Single(_sent);     // gone well before a chat box's 1.5 s
    }

    [Fact]
    public void R4_6_D154_EnterGoesAfterAtMostOneAndAHalfSecondsWhateverHappens()
    {
        EnterGate.Armed = true;
        Key(VK_RETURN, true);
        Thread.Sleep(EnterGate.MaxHoldMs + 400);   // the engine never said the fixes were in
        Assert.Single(_sent);
    }
}
