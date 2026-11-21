using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlobalAutocorrect.Core.Ai;

namespace GlobalAutocorrect.Llm;

/// <summary>
/// Talks to the local llama-server over loopback HTTP only (3.3, 10.5). The prompt is a setting (5.2).
/// Request and response text is never logged or stored (8.6).
/// </summary>
public sealed class LlamaServerClient : ILlmClient, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _http;

    /// <param name="apiKey">Per-launch key the host passes to llama-server (D21); null for none.</param>
    public LlamaServerClient(Uri baseAddress, PromptTemplate prompt, HttpMessageHandler? handler = null, TimeSpan? timeout = null, string? apiKey = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(prompt);
        if (!baseAddress.IsAbsoluteUri || !baseAddress.IsLoopback || baseAddress.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("The model server address must be an http loopback address.", nameof(baseAddress));

        Prompt = prompt;
        handler ??= new SocketsHttpHandler { UseProxy = false };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = timeout ?? DefaultTimeout,
        };
        if (apiKey is not null)
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>The clean-up prompt (5.2). Read on every request, so changing it takes effect immediately.</summary>
    public PromptTemplate Prompt { get; set; }

    public async Task<LlmResult> CleanAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(Prompt.BuildMessages(text)
                .Select(t => (JsonNode)new JsonObject { ["role"] = t.Role, ["content"] = t.Content }).ToArray()),
            ["temperature"] = 0,
            ["max_tokens"] = PromptTemplate.MaxTokensFor(text),
            ["cache_prompt"] = true,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            ["id_slot"] = CleanSlot,   // D159: the clean-up keeps its own slot (and prompt cache); suggestions use the other
        };

        var sw = Stopwatch.StartNew();
        Interlocked.Increment(ref _cleaning);
        try
        {
            using var resp = await _http.PostAsJsonAsync("/v1/chat/completions", body, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new LlmUnavailableException($"Model server returned HTTP {(int)resp.StatusCode}.");
            var json = await resp.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            string content = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            return new LlmResult(StripThinking(content).Trim(), sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmUnavailableException("Model server request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmUnavailableException("Model server could not be reached.", ex);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new LlmUnavailableException("Model server returned an unreadable reply.", ex);
        }
        finally { Interlocked.Decrement(ref _cleaning); }
    }

    private int _cleaning;

    /// <summary>
    /// D160 (benchmark on this PC: the clean-up took 667 ms instead of 419 with suggestions asked on the other slot; the
    /// two slots share the GPU): while a clean-up is running, a suggestion doesn't ask the model (the word list answers).
    /// </summary>
    public bool Cleaning => Volatile.Read(ref _cleaning) > 0;

    /// <summary>D159: the model's two slots: the clean-up's (its long prompt stays cached) and the suggestions'.</summary>
    public const int CleanSlot = 0, SuggestSlot = 1;

    /// <summary>D159: the likeliest next pieces of text after <paramref name="prompt"/>, on <see cref="SuggestSlot"/>.</summary>
    public async Task<IReadOnlyList<TokenChance>?> NextAsync(string prompt, int top, CancellationToken cancellationToken)
    {
        if (Cleaning) return null;   // D160: corrections first
        var body = new JsonObject
        {
            ["prompt"] = prompt,
            ["n_predict"] = 1,
            ["n_probs"] = top,
            ["temperature"] = 0,
            ["cache_prompt"] = true,
            ["id_slot"] = SuggestSlot,
        };
        try
        {
            using var resp = await _http.PostAsJsonAsync("/completion", body, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return ParseChances(await resp.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or FormatException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>
    /// D159: the first position's choices, in whichever form the server writes them: "top_logprobs" (token, logprob),
    /// "top_probs" (token, prob) or the older "probs" (tok_str, prob).
    /// </summary>
    public static IReadOnlyList<TokenChance>? ParseChances(JsonNode? json)
    {
        var first = json?["completion_probabilities"]?[0];
        if (first is null) return null;
        var list = new List<TokenChance>();
        if (first["top_logprobs"] is JsonArray logs)
            foreach (var e in logs)
                if (e?["token"]?.GetValue<string>() is { } t && e["logprob"] is { } lp) list.Add(new(t, Math.Exp(lp.GetValue<double>())));
        if (list.Count == 0 && first["top_probs"] is JsonArray probs)
            foreach (var e in probs)
                if (e?["token"]?.GetValue<string>() is { } t && e["prob"] is { } pr) list.Add(new(t, pr.GetValue<double>()));
        if (list.Count == 0 && first["probs"] is JsonArray old)
            foreach (var e in old)
                if (e?["tok_str"]?.GetValue<string>() is { } t && e["prob"] is { } pr) list.Add(new(t, pr.GetValue<double>()));
        return list.Count > 0 ? list : null;
    }

    /// <summary>True when GET /health returns 200. Any failure is false; only caller cancellation throws.</summary>
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await _http.GetAsync("/health", cancellationToken).ConfigureAwait(false);
            return resp.StatusCode == HttpStatusCode.OK;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();

    private static string StripThinking(string s)
    {
        const string Marker = "</think>";
        int end = s.IndexOf(Marker, StringComparison.Ordinal);
        return end >= 0 ? s[(end + Marker.Length)..] : s;
    }
}
