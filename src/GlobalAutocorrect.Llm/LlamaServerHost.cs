using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace GlobalAutocorrect.Llm;

/// <summary>Where the server and model live (5.2: model and quantisation are settings).</summary>
public sealed record ServerOptions(
    string ServerExe,
    string ModelPath,
    string? CpuServerExe = null,
    int GpuLayers = 99,
    int ContextSize = 4096,
    int StartTimeoutSeconds = 120,
    string? CpuModelPath = null,    // 5.3.c: the smaller model for the CPU fallback, if downloaded
    IReadOnlyList<string>? ExtraArgs = null);   // D56: more llama-server flags (speculative decoding)

/// <summary>D56: llama-server flags the app always adds.</summary>
public static class ServerFlags
{
    /// <summary>
    /// Lookup (n-gram) speculative decoding: a clean-up is mostly a copy of the input, so the server drafts the next
    /// tokens from text already in the prompt and the model checks them in one step. Measured on the tuning set: p50
    /// 339 → 193 ms, p95 519 → 352 ms, fixed 94.4% and false edits 0.20% unchanged. n=4 matching tokens before drafting,
    /// up to 24 drafted (n=3 was faster but lost two fixes; the defaults, n=12 m=48, saved only a fifth).
    /// </summary>
    public static IReadOnlyList<string> LookupDecoding { get; } =
        ["--spec-type", "ngram-simple", "--spec-ngram-simple-size-n", "4", "--spec-ngram-simple-size-m", "24"];
}

public enum ServerState { Stopped, Starting, Ready, Failed }

/// <summary>What the host learnt from the server log (5.8.a, 9.3.a). No text.</summary>
public sealed record ServerInfo(bool UsingGpu, string? Device, int OffloadedLayers, int TotalLayers, bool CpuFallback);

/// <summary>A started server process; abstracted so tests can fake it.</summary>
public interface IServerProcess : IDisposable
{
    bool HasExited { get; }
    IReadOnlyList<string> Log { get; }
    void Kill();
}

/// <summary>
/// Runs llama-server as a child process on 127.0.0.1 only (5.1.b) with a per-launch API key and no slots endpoint (D21).
/// Falls back to the CPU build if the CUDA build won't start (5.1.c).
/// </summary>
public sealed partial class LlamaServerHost : IAsyncDisposable
{
    private readonly ServerOptions _o;
    private readonly Func<ProcessStartInfo, IServerProcess> _start;
    private readonly Func<Uri, CancellationToken, Task<bool>> _healthy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IServerProcess? _proc;

    public ServerState State { get; private set; } = ServerState.Stopped;
    public Uri? BaseAddress { get; private set; }
    public string ApiKey { get; private set; } = "";
    public ServerInfo? Info { get; private set; }

    public LlamaServerHost(ServerOptions options,
        Func<ProcessStartInfo, IServerProcess>? start = null,
        Func<Uri, CancellationToken, Task<bool>>? healthy = null)
    {
        _o = options;
        _realProcesses = start is null;
        _start = start ?? (psi => new ServerProcess(psi));
        _healthy = healthy ?? DefaultHealthy;
    }

    private readonly bool _realProcesses;

    public bool IsAlive => State == ServerState.Ready && _proc is { HasExited: false };

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsAlive) return true;
            StopCore();
            State = ServerState.Starting;
            if (await TryStartAsync(_o.ServerExe, _o.ModelPath, _o.GpuLayers, cpuFallback: false, ct).ConfigureAwait(false)) return true;
            string cpuModel = _o.CpuModelPath is { } m && (!_realProcesses || File.Exists(m)) ? m : _o.ModelPath;
            if (_o.CpuServerExe is not null && await TryStartAsync(_o.CpuServerExe, cpuModel, 0, cpuFallback: true, ct).ConfigureAwait(false)) return true;
            State = ServerState.Failed;
            return false;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { StopCore(); }
        finally { _gate.Release(); }
    }

    /// <summary>One watchdog probe (5.6.a): true when the process is up and /health answers.</summary>
    public async Task<bool> ProbeAsync(CancellationToken ct)
    {
        if (_proc is null || _proc.HasExited || BaseAddress is null) return false;
        return await _healthy(BaseAddress, ct).ConfigureAwait(false);
    }

    private async Task<bool> TryStartAsync(string exe, string modelPath, int gpuLayers, bool cpuFallback, CancellationToken ct)
    {
        if (_realProcesses && !File.Exists(exe)) return false;
        int port = FreePort();
        ApiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var psi = new ProcessStartInfo(exe)
        {
            ArgumentList =
            {
                "-m", modelPath, "--host", "127.0.0.1", "--port", port.ToString(),
                "-ngl", gpuLayers.ToString(), "-c", _o.ContextSize.ToString(), "-np", "1",
                "--reasoning-budget", "0", "-lv", "4", "--no-slots", "--api-key", ApiKey,
            },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in _o.ExtraArgs ?? []) psi.ArgumentList.Add(a);
        IServerProcess proc;
        try { proc = _start(psi); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException) { return false; }

        var address = new Uri($"http://127.0.0.1:{port}");
        var deadline = DateTime.UtcNow.AddSeconds(_o.StartTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (proc.HasExited) break;
            if (await _healthy(address, ct).ConfigureAwait(false))
            {
                _proc = proc;
                BaseAddress = address;
                Info = ParseInfo(proc.Log, cpuFallback);
                State = ServerState.Ready;
                return true;
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        try { proc.Kill(); } catch (InvalidOperationException) { }
        proc.Dispose();
        return false;
    }

    /// <summary>Reads the evidence for 5.8.a from the -lv 4 log: device in use and offloaded layers.</summary>
    public static ServerInfo ParseInfo(IReadOnlyList<string> log, bool cpuFallback)
    {
        string? device = null;
        int off = 0, total = 0;
        foreach (var l in log)
        {
            var d = DeviceLine().Match(l);
            if (d.Success) device = d.Groups[1].Value.Trim();
            var o = OffloadLine().Match(l);
            if (o.Success) { off = int.Parse(o.Groups[1].Value); total = int.Parse(o.Groups[2].Value); }
        }
        return new ServerInfo(!cpuFallback && device is not null && off > 0, device, off, total, cpuFallback);
    }

    [GeneratedRegex(@"using device (CUDA\d+ \([^)]*\))")]
    private static partial Regex DeviceLine();

    [GeneratedRegex(@"offloaded (\d+)/(\d+) layers to GPU")]
    private static partial Regex OffloadLine();

    private void StopCore()
    {
        if (_proc is not null)
        {
            try { if (!_proc.HasExited) _proc.Kill(); } catch (InvalidOperationException) { }
            _proc.Dispose();
            _proc = null;
        }
        BaseAddress = null;
        State = ServerState.Stopped;
    }

    private static async Task<bool> DefaultHealthy(Uri baseAddress, CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var r = await http.GetAsync(new Uri(baseAddress, "/health"), ct).ConfigureAwait(false);
            return r.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return false; }
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
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private sealed class ServerProcess : IServerProcess
    {
        private readonly Process _p;
        private readonly List<string> _log = [];

        public ServerProcess(ProcessStartInfo psi)
        {
            _p = Process.Start(psi) ?? throw new InvalidOperationException("llama-server did not start");
            KillOnCloseJob.Assign(_p);   // 5.6: never outlives the app, even after a crash
            _p.ErrorDataReceived += (_, e) => Add(e.Data);
            _p.OutputDataReceived += (_, e) => Add(e.Data);
            _p.BeginErrorReadLine();
            _p.BeginOutputReadLine();
        }

        private bool _capturing = true;

        // Only start-up lines are kept: device and offload info come before "listening". Capture stops there, so
        // request text a verbose server might print never reaches memory, let alone disk (8.6).
        private void Add(string? line)
        {
            if (line is null) return;
            lock (_log)
            {
                if (!_capturing || _log.Count >= 400) return;
                _log.Add(line);
                if (line.Contains("listening on", StringComparison.Ordinal)) _capturing = false;
            }
        }

        public bool HasExited => _p.HasExited;
        public IReadOnlyList<string> Log { get { lock (_log) return [.. _log]; } }
        public void Kill() => _p.Kill(entireProcessTree: true);
        public void Dispose() => _p.Dispose();
    }
}
