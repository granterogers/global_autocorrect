using System.Text.Json;
using GlobalAutocorrect.Core.Ai;
using GlobalAutocorrect.Llm;
using Xunit;

namespace GlobalAutocorrect.Llm.Tests;

/// <summary>Model tier (11.1.b): the real llama-server and model on this PC's GPU. Not part of the fast tier.</summary>
[Trait("Category", "Model")]
public sealed class ModelTierTests
{
    private static async Task<(LlamaServerHost Host, LlamaServerClient Client)> Start()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "GlobalAutocorrect.slnx"))) d = d.Parent;
        string build = JsonDocument.Parse(File.ReadAllText(Path.Combine(d!.FullName, "scripts", "manifest.json"))).RootElement.GetProperty("llama").GetProperty("build").GetString()!;
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlobalAutocorrect", "cache");
        string exe = Path.Combine(cache, "llama", build, "cuda", "llama-server.exe"), model = Path.Combine(cache, "models", "gemma-4-E4B-it-Q5_K_M.gguf");
        if (!File.Exists(exe) || !File.Exists(model)) Assert.Skip("Model not downloaded; run scripts/setup.ps1");
        var host = new LlamaServerHost(new ServerOptions(exe, model));
        Assert.True(await host.StartAsync(TestContext.Current.CancellationToken), "server did not start");
        return (host, new LlamaServerClient(host.BaseAddress!, PromptTemplate.Default, apiKey: host.ApiKey));
    }

    [Fact]
    public async Task R5_8_e_CleansATestSentence_R5_8_a()
    {
        var (host, client) = await Start();
        await using var _ = host;
        using var __ = client;
        Assert.True(host.Info!.UsingGpu, "not on the GPU (5.8.a)");
        var r = await client.CleanAsync("I saw teh cat yesterday", TestContext.Current.CancellationToken);
        Assert.Contains("the cat", r.Text);
    }

    [Fact]
    public async Task R5_5_MedianUnder2sOnGpu()
    {
        var (host, client) = await Start();
        await using var _ = host;
        using var __ = client;
        await client.CleanAsync("warm up teh cache", TestContext.Current.CancellationToken);
        var ms = new List<double>();
        for (int i = 0; i < 10; i++)
            ms.Add((await client.CleanAsync($"Message {i}: i wnet to the shops and forgot teh milk again.", TestContext.Current.CancellationToken)).LatencyMs);
        ms.Sort();
        double median = (ms[4] + ms[5]) / 2;
        Assert.True(median < 2000, $"median {median:F0} ms");
    }
}
