using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlobalAutocorrect.Core.Ai;

namespace GlobalAutocorrect.Eval;

/// <summary>Spike A only: runs llama-server on 127.0.0.1 and sends clean-up requests. The product host is T5.2.</summary>
internal sealed class SpikeServer : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly HttpClient _http;
    private readonly List<string> _log = [];

    public IReadOnlyList<string> Log { get { lock (_log) return [.. _log]; } }
    public int ProcessId => _proc.Id;

    private SpikeServer(Process proc, int port)
    {
        _proc = proc;
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
    }

    public static async Task<SpikeServer> StartAsync(string serverExe, string modelPath, int gpuLayers = 99)
    {
        int port = FreePort();
        var psi = new ProcessStartInfo(serverExe)
        {
            ArgumentList =
            {
                "-m", modelPath, "--host", "127.0.0.1", "--port", port.ToString(), "-ngl", gpuLayers.ToString(),
                "-c", "4096", "-np", "1", "--reasoning-budget", "0",
            },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("llama-server did not start");
        var s = new SpikeServer(proc, port);
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (s._log) s._log.Add(e.Data); };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (s._log) s._log.Add(e.Data); };
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();

        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            if (proc.HasExited) throw new InvalidOperationException("llama-server exited:\n" + string.Join('\n', s.Log.TakeLast(30)));
            try
            {
                using var r = await s._http.GetAsync("/health");
                if (r.StatusCode == HttpStatusCode.OK) return s;
            }
            catch (HttpRequestException) { }
            await Task.Delay(250);
        }
        throw new TimeoutException("llama-server did not become healthy in 3 minutes");
    }

    public async Task<(string Text, double Ms, JsonNode? Timings)> CleanAsync(PromptTemplate prompt, string text)
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(prompt.BuildMessages(text)
                .Select(t => (JsonNode)new JsonObject { ["role"] = t.Role, ["content"] = t.Content }).ToArray()),
            ["temperature"] = 0,
            ["max_tokens"] = PromptTemplate.MaxTokensFor(text),
            ["cache_prompt"] = true,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
        };
        var sw = Stopwatch.StartNew();
        using var resp = await _http.PostAsJsonAsync("/v1/chat/completions", body);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonNode>() ?? throw new InvalidDataException("empty response");
        sw.Stop();
        string content = json["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        return (StripThinking(content).Trim(), sw.Elapsed.TotalMilliseconds, json["timings"]);
    }

    private static string StripThinking(string s)
    {
        int end = s.IndexOf("</think>", StringComparison.Ordinal);
        return end >= 0 ? s[(end + 8)..] : s;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        if (!_proc.HasExited)
        {
            _proc.Kill(entireProcessTree: true);
            await _proc.WaitForExitAsync();
        }
        _proc.Dispose();
    }

    public static JsonSerializerOptions JsonOut { get; } = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
