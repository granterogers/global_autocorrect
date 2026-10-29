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
        };

        var sw = Stopwatch.StartNew();
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
