using System.Diagnostics;
using GlobalAutocorrect.Core.Ai;
using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Telemetry;
using GlobalAutocorrect.Llm;
using Xunit;

namespace GlobalAutocorrect.Llm.Tests;

/// <summary>D67: the model starts reloading after an idle unload as soon as the user is about to type.</summary>
public sealed class EarlyReloadTests
{
    private sealed class FakeProcess : IServerProcess
    {
        public bool HasExited { get; set; }
        public IReadOnlyList<string> Log { get; } = [];
        public void Kill() => HasExited = true;
        public void Dispose() { }
    }

    /// <summary>A fake llama-server whose load can be held "in progress" until the test lets it finish.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public List<FakeProcess> Procs { get; } = [];
        public volatile bool Hold;
        public ManualClock Clock { get; } = new();
        public Timings Timings { get; } = new();
        public bool Paused, OwnWindow, Correctable = true;
        public int Reads;
        public LlmRuntime Llm { get; }
        public EarlyReload Reload { get; }

        public Rig(Action<Action>? background = null, Func<bool>? read = null)
        {
            var host = new LlamaServerHost(new ServerOptions("cuda\\llama-server.exe", "model.gguf", StartTimeoutSeconds: 30),
                _ => { var p = new FakeProcess(); lock (Procs) Procs.Add(p); return p; },
                (_, _) => Task.FromResult(!Hold));
            Llm = new LlmRuntime(host, Clock, () => PromptTemplate.Default) { IdleUnloadMinutes = 15 };
            Reload = new EarlyReload(Llm, Clock, () => Paused, () => OwnWindow, read ?? (() => { Reads++; return Correctable; }),
                Timings, background ?? (a => a()));
        }

        public int Starts { get { lock (Procs) return Procs.Count; } }

        /// <summary>Loaded at app start, then unloaded after 16 quiet minutes (5.6.b).</summary>
        public async Task UnloadedAfterIdle()
        {
            Assert.True(await Llm.StartAsync());
            Clock.Advance(16 * 60_000);
            await Llm.MaintainAsync(CancellationToken.None);
            Assert.Equal(ServerState.Stopped, Llm.Host.State);
        }

        public async Task WaitReady()
        {
            for (int i = 0; i < 200 && !Llm.Host.IsAlive; i++) await Task.Delay(5);
            Assert.True(Llm.Host.IsAlive);
        }

        public async Task<bool> Recorded(string stage)
        {
            for (int i = 0; i < 200 && !Timings.Snapshot().ContainsKey(stage); i++) await Task.Delay(5);
            return Timings.Snapshot().ContainsKey(stage);
        }

        public ValueTask DisposeAsync() => Llm.DisposeAsync();
    }

    [Fact]
    public async Task R5_6_b_D67_FocusIntoACorrectableFieldAfterUnloadStartsALoad()
    {
        await using var r = new Rig();
        await r.UnloadedAfterIdle();
        r.Reload.OnFocusChanged();
        Assert.Equal(2, r.Starts);   // app start, then this one: no key needed
        await r.WaitReady();
    }

    [Fact]
    public async Task R8_3_R8_4_D67_FocusIntoAFieldTheAppWouldNotCorrectDoesNotLoad()
    {
        // Password fields, excluded apps and elevated windows all read as "not correctable" (UiaTextTarget.Correctable).
        await using var r = new Rig { Correctable = false };
        await r.UnloadedAfterIdle();
        for (int i = 0; i < 5; i++) r.Reload.OnFocusChanged();
        Assert.Equal(1, r.Starts);
        Assert.Equal(ServerState.Stopped, r.Llm.Host.State);
    }

    [Fact]
    public async Task R5_6_b_D67_TheAppsOwnWindowsDoNotLoad()
    {
        await using var r = new Rig { OwnWindow = true };
        await r.UnloadedAfterIdle();
        r.Reload.OnFocusChanged();
        r.Reload.OnKey();   // typing in Settings, say
        Assert.Equal(1, r.Starts);
        Assert.Equal(0, r.Reads);
    }

    [Fact]
    public async Task R8_2_D67_NothingLoadsWhilePaused()
    {
        await using var r = new Rig { Paused = true };
        await r.UnloadedAfterIdle();
        r.Reload.OnFocusChanged();
        for (int i = 0; i < 10; i++) r.Reload.OnKey();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, r.Starts);
        Assert.Equal(0, r.Reads);

        r.Paused = false;   // resumed: the next sign of typing loads it
        r.Reload.OnFocusChanged();
        Assert.Equal(2, r.Starts);
    }

    [Fact]
    public async Task R12_3_D67_ALoadedModelCostsNoFieldReads()
    {
        var queued = new List<Action>();
        await using var r = new Rig(background: queued.Add);
        Assert.True(await r.Llm.StartAsync());
        for (int i = 0; i < 20; i++) r.Reload.OnFocusChanged();
        Assert.Empty(queued);   // not even a background job
        Assert.Equal(0, r.Reads);
        Assert.Equal(1, r.Starts);
    }

    [Fact]
    public async Task R5_6_b_D67_RepeatedFocusChangesStartAtMostOneLoad()
    {
        await using var r = new Rig();
        await r.UnloadedAfterIdle();
        r.Hold = true;   // the load takes a while
        for (int i = 0; i < 30; i++) r.Reload.OnFocusChanged();   // tabbing between fields
        Assert.Equal(2, r.Starts);
        Assert.Equal(1, r.Reads);   // once loading, focus changes cost nothing
        r.Hold = false;
        await r.WaitReady();
        for (int i = 0; i < 30; i++) r.Reload.OnFocusChanged();
        await r.Llm.MaintainAsync(CancellationToken.None);
        Assert.Equal(2, r.Starts);
        Assert.True(r.Llm.Host.IsAlive);
    }

    [Fact]
    public async Task R5_6_b_D67_FocusChangesDuringAFieldReadAreCoalesced()
    {
        var queued = new List<Action>();
        await using var r = new Rig(background: queued.Add) { Correctable = false };
        await r.UnloadedAfterIdle();
        for (int i = 0; i < 25; i++) r.Reload.OnFocusChanged();   // focus flickers while the first read is out
        Assert.Single(queued);
        queued[0]();
        Assert.Equal(2, r.Reads);   // the first field, then the one focus ended on
        r.Reload.OnFocusChanged();
        Assert.Equal(2, queued.Count);   // a new read once the last one is done
    }

    [Fact]
    public async Task R5_6_b_D67_ALoadInProgressIsNotRestarted()
    {
        await using var r = new Rig();
        await r.UnloadedAfterIdle();
        r.Hold = true;
        r.Reload.OnFocusChanged();
        Assert.Equal(ServerState.Starting, r.Llm.Host.State);
        Assert.False(r.Llm.Wake());
        for (int i = 0; i < 10; i++) r.Reload.OnKey();   // the user starts typing before it has finished
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, r.Starts);
        Assert.False(r.Procs[1].HasExited);
        r.Hold = false;
        await r.WaitReady();
        Assert.Equal(2, r.Starts);
    }

    [Fact]
    public async Task R5_6_b_D67_AfterAFocusLoadTheIdleUnloadComesAfterTheFullIdleTime()
    {
        await using var r = new Rig();
        await r.UnloadedAfterIdle();
        r.Reload.OnFocusChanged();
        await r.WaitReady();
        await r.Llm.MaintainAsync(CancellationToken.None);   // the next maintenance pass must not unload it straight away
        Assert.True(r.Llm.Host.IsAlive);

        r.Clock.Advance(10 * 60_000);
        r.Reload.OnFocusChanged();   // focus moves about but nothing is typed: not activity
        r.Clock.Advance(4 * 60_000);
        await r.Llm.MaintainAsync(CancellationToken.None);
        Assert.True(r.Llm.Host.IsAlive);   // 14 minutes after the load
        r.Clock.Advance(2 * 60_000);
        await r.Llm.MaintainAsync(CancellationToken.None);
        Assert.Equal(ServerState.Stopped, r.Llm.Host.State);   // 16 minutes: unloaded as usual
        Assert.Equal(2, r.Starts);
    }

    [Fact]
    public async Task R12_3_D67_FocusAndKeysNeverWaitForTheFieldReadOrTheLoad()
    {
        using var release = new ManualResetEventSlim();
        // The field read runs on the thread pool, as in the app, and is slow (a busy app); the load is slow too.
        await using var r = new Rig(background: a => ThreadPool.QueueUserWorkItem(_ => a()), read: () => { release.Wait(2000); return true; });
        await r.UnloadedAfterIdle();
        r.Hold = true;
        var sw = Stopwatch.StartNew();
        r.Reload.OnFocusChanged();
        r.Reload.OnKey();
        r.Reload.OnFocusChanged();
        Assert.True(sw.ElapsedMilliseconds < 100, $"{sw.ElapsedMilliseconds} ms");
        release.Set();
        r.Hold = false;
        await r.WaitReady();
    }

    [Fact]
    public async Task R12_1_D67_TheWaitAfterAnUnloadIsTimed_NoneWhenTheEarlyLoadWon()
    {
        await using var r = new Rig();
        await r.UnloadedAfterIdle();
        r.Reload.OnFocusChanged();
        await r.WaitReady();
        Assert.True(await r.Recorded("model.load"));
        r.Clock.Advance(2_000);
        r.Reload.OnKey();
        Assert.Equal(0, r.Timings.Snapshot()["model.reloadWait"].Max);
        r.Clock.Advance(900);
        r.Reload.OnKey();
        r.Reload.OnAiFix();
        r.Reload.OnAiFix();   // only the first fix after the reload counts
        var s = r.Timings.Snapshot();
        Assert.Equal((1, 900.0), (s["model.reloadKeyToFix"].Count, s["model.reloadKeyToFix"].Max));
        Assert.Equal(1, s["model.reloadWait"].Count);
    }

    [Fact]
    public async Task R12_1_D67_TheWaitAfterAnUnloadIsTimed_FromTheFirstKey()
    {
        await using var r = new Rig();
        await r.UnloadedAfterIdle();
        r.Hold = true;
        r.Reload.OnKey();   // no focus change first: the key starts the load
        Assert.Equal(2, r.Starts);
        r.Clock.Advance(2_500);
        r.Hold = false;
        Assert.True(await r.Recorded("model.reloadWait"));
        Assert.Equal(2_500, r.Timings.Snapshot()["model.reloadWait"].Max);
        r.Clock.Advance(500);
        r.Reload.OnAiFix();
        Assert.Equal(3_000, r.Timings.Snapshot()["model.reloadKeyToFix"].Max);
    }
}
