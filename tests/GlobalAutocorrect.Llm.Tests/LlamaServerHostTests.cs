using System.Diagnostics;
using GlobalAutocorrect.Core.Ai;
using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Llm;
using Xunit;

namespace GlobalAutocorrect.Llm.Tests;

public sealed class LlamaServerHostTests
{
    private sealed class FakeProcess(IReadOnlyList<string> log) : IServerProcess
    {
        public bool HasExited { get; set; }
        public IReadOnlyList<string> Log { get; } = log;
        public void Kill() => HasExited = true;
        public void Dispose() { }
    }

    private static readonly string[] GpuLog =
    [
        "I llama_prepare_model_devices: using device CUDA0 (NVIDIA RTX PRO 2000 Blackwell Generation Laptop GPU) (0000:01:00.0) - 7043 MiB free",
        "I load_tensors: offloaded 33/33 layers to GPU",
        "I srv server_on_li: listening on http://127.0.0.1:1234",
    ];

    private sealed class Fake
    {
        public List<ProcessStartInfo> Starts { get; } = [];
        public List<FakeProcess> Procs { get; } = [];
        public Func<ProcessStartInfo, bool> Healthy { get; set; } = _ => true;
        public bool HealthOverride { get; set; } = true;

        public LlamaServerHost Host(string? cpu = "cpu\\llama-server.exe") =>
            new(new ServerOptions("cuda\\llama-server.exe", "model.gguf", cpu, StartTimeoutSeconds: 1),
                psi =>
                {
                    Starts.Add(psi);
                    var p = new FakeProcess(GpuLog);
                    Procs.Add(p);
                    return p;
                },
                (_, _) => Task.FromResult(HealthOverride && Healthy(Starts[^1])));
    }

    [Fact]
    public async Task R5_1_b_BindsToLoopbackWithApiKeyAndNoSlots()
    {
        var f = new Fake();
        await using var host = f.Host();
        Assert.True(await host.StartAsync(TestContext.Current.CancellationToken));
        var args = f.Starts[0].ArgumentList;
        Assert.Equal("127.0.0.1", args[args.IndexOf("--host") + 1]);
        Assert.Equal(host.ApiKey, args[args.IndexOf("--api-key") + 1]);
        Assert.Equal(32, host.ApiKey.Length);
        Assert.Contains("--no-slots", args);
        Assert.Equal("0", args[args.IndexOf("--reasoning-budget") + 1]);   // 12.8
        Assert.True(host.BaseAddress!.IsLoopback);
    }

    [Fact]
    public async Task R5_8_a_ReadsDeviceAndOffloadFromLog()
    {
        var f = new Fake();
        await using var host = f.Host();
        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(host.Info!.UsingGpu);
        Assert.StartsWith("CUDA0 (NVIDIA RTX PRO 2000", host.Info.Device);
        Assert.Equal((33, 33), (host.Info.OffloadedLayers, host.Info.TotalLayers));
    }

    [Fact]
    public void R5_8_a_NoCudaLineMeansNotUsingGpu()
    {
        var info = LlamaServerHost.ParseInfo(["I srv llama_server: initializing ..."], cpuFallback: false);
        Assert.False(info.UsingGpu);
    }

    [Fact]
    public async Task R5_1_c_FallsBackToCpuBuildWhenCudaWontStart()
    {
        var f = new Fake { Healthy = psi => psi.FileName.StartsWith("cpu", StringComparison.Ordinal) };
        await using var host = f.Host();
        Assert.True(await host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, f.Starts.Count);
        Assert.True(host.Info!.CpuFallback);
        Assert.Equal("0", f.Starts[1].ArgumentList[f.Starts[1].ArgumentList.IndexOf("-ngl") + 1]);
    }

    [Fact]
    public async Task R5_3_c_CpuFallbackUsesTheSmallerModel()
    {
        var f = new Fake { Healthy = psi => psi.FileName.StartsWith("cpu", StringComparison.Ordinal) };
        await using var host = new LlamaServerHost(
            new ServerOptions("cuda\\llama-server.exe", "e4b.gguf", "cpu\\llama-server.exe", StartTimeoutSeconds: 1, CpuModelPath: "e2b.gguf"),
            psi => { f.Starts.Add(psi); return new FakeProcess([]); },
            (_, _) => Task.FromResult(f.Healthy(f.Starts[^1])));
        Assert.True(await host.StartAsync(TestContext.Current.CancellationToken));
        var args = f.Starts[^1].ArgumentList;
        Assert.Equal("e2b.gguf", args[args.IndexOf("-m") + 1]);
    }

    [Fact]
    public async Task R5_6_a_WatchdogRestartsAfterThreeFailedProbes()
    {
        var f = new Fake();
        var clock = new ManualClock();
        await using var rt = new LlmRuntime(f.Host(), clock, () => PromptTemplate.Default);
        Assert.True(await rt.Host.StartAsync(TestContext.Current.CancellationToken));

        f.HealthOverride = false;   // stops answering
        await rt.MaintainAsync(TestContext.Current.CancellationToken);
        await rt.MaintainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, rt.Restarts);   // two misses are tolerated
        await rt.MaintainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, rt.Restarts);
    }

    [Fact]
    public async Task R5_6_a_WatchdogRestartsAfterCrash()
    {
        var f = new Fake();
        var clock = new ManualClock();
        await using var rt = new LlmRuntime(f.Host(), clock, () => PromptTemplate.Default);
        await rt.Host.StartAsync(TestContext.Current.CancellationToken);
        f.Procs[0].HasExited = true;   // crashed
        for (int i = 0; i < 3; i++) await rt.MaintainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, rt.Restarts);
    }

    [Fact]
    public void R5_6_TheServerProcessIsTiedToTheAppsLifetime()
    {
        using var p = Process.Start(new ProcessStartInfo("ping", "-n 3 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            Assert.True(KillOnCloseJob.Assign(p));
            Assert.True(KillOnCloseJob.Contains(p));
        }
        finally { if (!p.HasExited) p.Kill(); }
    }

    [Fact]
    public async Task R5_6_a_AFailedStartIsNotRetriedOnEveryKeystroke()
    {
        var f = new Fake { HealthOverride = false };   // the server never comes up (e.g. model not downloaded)
        var clock = new ManualClock();
        await using var rt = new LlmRuntime(f.Host(cpu: null), clock, () => PromptTemplate.Default);
        rt.NotifyActivity();
        for (int i = 0; i < 100 && f.Starts.Count == 0; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        for (int i = 0; i < 150 && rt.Host.State != ServerState.Failed; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        int starts = f.Starts.Count;
        for (int i = 0; i < 20; i++) rt.NotifyActivity();   // twenty keystrokes
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(starts, f.Starts.Count);
        clock.Advance(61_000);   // a minute later it may try again
        rt.NotifyActivity();
        for (int i = 0; i < 100 && f.Starts.Count == starts; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(f.Starts.Count > starts);
    }

    [Fact]
    public async Task R5_6_b_UnloadsAfterIdleAndReloadsOnNextKey()
    {
        var f = new Fake();
        var clock = new ManualClock();
        await using var rt = new LlmRuntime(f.Host(), clock, () => PromptTemplate.Default) { IdleUnloadMinutes = 15 };
        await rt.Host.StartAsync(TestContext.Current.CancellationToken);

        clock.Advance(14 * 60_000);
        await rt.MaintainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ServerState.Ready, rt.Host.State);
        clock.Advance(2 * 60_000);
        await rt.MaintainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ServerState.Stopped, rt.Host.State);

        var sw = Stopwatch.StartNew();
        rt.NotifyActivity();   // must not block the keystroke
        Assert.True(sw.ElapsedMilliseconds < 100);
        for (int i = 0; i < 50 && rt.Host.State != ServerState.Ready; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(ServerState.Ready, rt.Host.State);
    }
}
