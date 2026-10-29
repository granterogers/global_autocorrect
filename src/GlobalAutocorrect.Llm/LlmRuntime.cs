using GlobalAutocorrect.Core.Ai;
using GlobalAutocorrect.Core.Engine;

namespace GlobalAutocorrect.Llm;

/// <summary>
/// The model as the engine sees it (5.6): starts the server on demand, restarts it when it crashes or stops answering
/// (5.6.a), unloads it after a quiet spell and reloads on the next keystroke without blocking typing (5.6.b).
/// </summary>
public sealed class LlmRuntime : ILlmClient, IAsyncDisposable
{
    private readonly LlamaServerHost _host;
    private readonly IClock _clock;
    private readonly Func<PromptTemplate> _prompt;
    private LlamaServerClient? _client;
    private Task<bool>? _starting;
    private long _lastActivityMs;
    private int _failedProbes;
    private readonly object _lock = new();

    public int IdleUnloadMinutes { get; set; } = 15;
    public int Restarts { get; private set; }
    public LlamaServerHost Host => _host;

    public LlmRuntime(LlamaServerHost host, IClock clock, Func<PromptTemplate> prompt)
    {
        _host = host;
        _clock = clock;
        _prompt = prompt;
        _lastActivityMs = clock.NowMs;
    }

    /// <summary>Called on every physical keystroke. Starts a load in the background if the model is unloaded (5.6.b).</summary>
    public void NotifyActivity()
    {
        _lastActivityMs = _clock.NowMs;
        if (!_host.IsAlive) _ = EnsureStarting();
    }

    public async Task<LlmResult> CleanAsync(string text, CancellationToken cancellationToken)
    {
        _lastActivityMs = _clock.NowMs;
        if (!_host.IsAlive && !await EnsureStarting().WaitAsync(cancellationToken).ConfigureAwait(false))
            throw new LlmUnavailableException("The model is not running.");
        var client = GetClient();
        client.Prompt = _prompt();
        return await client.CleanAsync(text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One watchdog/idle pass, run every few seconds by the host app.</summary>
    public async Task MaintainAsync(CancellationToken ct)
    {
        if (_host.State == ServerState.Ready)
        {
            if (_clock.NowMs - _lastActivityMs > IdleUnloadMinutes * 60_000L)
            {
                await _host.StopAsync().ConfigureAwait(false);   // 5.6.b: free the GPU while idle
                ResetClient();
                return;
            }
            if (await _host.ProbeAsync(ct).ConfigureAwait(false)) { _failedProbes = 0; return; }
            if (++_failedProbes < 3) return;
            // 5.6.a: crashed or stopped answering three times in a row → restart.
            _failedProbes = 0;
            Restarts++;
            await _host.StopAsync().ConfigureAwait(false);
            ResetClient();
            _ = EnsureStarting();   // in the background; the next request waits for it
        }
    }

    /// <summary>After a failed start, wait this long before trying again (a missing model must not relaunch per key).</summary>
    public int RetryAfterFailureMs { get; set; } = 60_000;

    private Task<bool> EnsureStarting()
    {
        lock (_lock)
        {
            if (_starting is { IsCompleted: false }) return _starting;
            if (_starting is { IsCompleted: true } last && !last.Result && _host.State == ServerState.Failed
                && _clock.NowMs - _failedAtMs < RetryAfterFailureMs)
                return last;
            ResetClient();
            _starting = _host.StartAsync(CancellationToken.None);
            _starting.ContinueWith(t => { if (!t.IsCompletedSuccessfully || !t.Result) _failedAtMs = _clock.NowMs; }, TaskScheduler.Default);
            return _starting;
        }
    }

    private long _failedAtMs = long.MinValue / 2;

    private LlamaServerClient GetClient()
    {
        lock (_lock)
        {
            return _client ??= new LlamaServerClient(_host.BaseAddress!, _prompt(), apiKey: _host.ApiKey);
        }
    }

    private void ResetClient()
    {
        lock (_lock)
        {
            _client?.Dispose();
            _client = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        ResetClient();
        await _host.DisposeAsync().ConfigureAwait(false);
    }
}
