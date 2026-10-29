using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GlobalAutocorrect.Core.Ai;
using GlobalAutocorrect.Llm;
using Xunit;

namespace GlobalAutocorrect.Llm.Tests;

public sealed class LlamaServerClientTests
{
    private static readonly Uri Base = new("http://127.0.0.1:8080");

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<JsonNode?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
                Bodies.Add(JsonNode.Parse(await request.Content.ReadAsStringAsync(ct)));
            return await respond(request, ct);
        }
    }

    private static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private static string Reply(string content) =>
        new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content } }) }.ToJsonString();

    private static FakeHandler Replying(string content) => new((_, _) => Task.FromResult(Json(Reply(content))));

    private static FakeHandler Unused() => new((_, _) => throw new InvalidOperationException("no request expected"));

    [Fact]
    public void R3_3_LoopbackOnly()
    {
        var prompt = PromptTemplate.Default;
        Assert.Throws<ArgumentException>(() => new LlamaServerClient(new Uri("http://192.168.1.5:8080"), prompt, Unused()));
        Assert.Throws<ArgumentException>(() => new LlamaServerClient(new Uri("https://127.0.0.1"), prompt, Unused()));
        using var a = new LlamaServerClient(new Uri("http://localhost:1234"), prompt, Unused());
        using var b = new LlamaServerClient(new Uri("http://127.0.0.1:1234"), prompt, Unused());
    }

    [Fact]
    public async Task R10_5_PostsChatCompletionAndReturnsContent()
    {
        const string input = "cna you sned this";
        HttpMethod? method = null;
        string? path = null;
        var handler = new FakeHandler((req, _) =>
        {
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            return Task.FromResult(Json(Reply(" Fixed text. ")));
        });
        using var client = new LlamaServerClient(Base, PromptTemplate.Default, handler);

        var result = await client.CleanAsync(input, CancellationToken.None);

        Assert.Equal("Fixed text.", result.Text);
        Assert.True(result.LatencyMs >= 0);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/v1/chat/completions", path);
        var body = Assert.Single(handler.Bodies)!;
        Assert.Equal(0, body["temperature"]!.GetValue<double>());
        Assert.True(body["cache_prompt"]!.GetValue<bool>());
        Assert.False(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal(PromptTemplate.MaxTokensFor(input), body["max_tokens"]!.GetValue<int>());
        var messages = body["messages"]!.AsArray();
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("user", messages[^1]!["role"]!.GetValue<string>());
        Assert.Equal(input, messages[^1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task R5_4_ThinkingDisabled()
    {
        var handler = Replying("<think>x</think>Clean");
        using var client = new LlamaServerClient(Base, PromptTemplate.Default, handler);

        var result = await client.CleanAsync("clen", CancellationToken.None);

        Assert.Equal("Clean", result.Text);
        Assert.False(Assert.Single(handler.Bodies)!["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
    }

    [Fact]
    public async Task R5_2_PromptIsASetting()
    {
        var handler = Replying("ok");
        using var client = new LlamaServerClient(Base, PromptTemplate.Default, handler);

        await client.CleanAsync("one", CancellationToken.None);
        client.Prompt = new PromptTemplate("A different system prompt.", []);
        await client.CleanAsync("two", CancellationToken.None);

        Assert.Equal(PromptTemplate.Default.System, handler.Bodies[0]!["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("A different system prompt.", handler.Bodies[1]!["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task R10_5_ServerErrorThrowsLlmUnavailable()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.InternalServerError)));
        using var client = new LlamaServerClient(Base, PromptTemplate.Default, handler);

        await Assert.ThrowsAsync<LlmUnavailableException>(() => client.CleanAsync("text", CancellationToken.None));
    }

    [Fact]
    public async Task R10_5_HealthFalseWhenDown()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("down"));
        using var client = new LlamaServerClient(Base, PromptTemplate.Default, handler);

        Assert.False(await client.IsHealthyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task R10_5_HealthTrueWhenOk()
    {
        string? path = null;
        var handler = new FakeHandler((req, _) =>
        {
            path = req.RequestUri!.AbsolutePath;
            return Task.FromResult(Json("{\"status\":\"ok\"}"));
        });
        using var client = new LlamaServerClient(Base, PromptTemplate.Default, handler);

        Assert.True(await client.IsHealthyAsync(CancellationToken.None));
        Assert.Equal("/health", path);
    }

    [Fact]
    public async Task R10_5_TimeoutThrowsLlmUnavailable()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        });
        using var client = new LlamaServerClient(Base, PromptTemplate.Default, handler, TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<LlmUnavailableException>(() => client.CleanAsync("text", CancellationToken.None));
    }

    [Fact]
    public async Task R10_5_CancellationPropagates()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        });
        using var client = new LlamaServerClient(Base, PromptTemplate.Default, handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CleanAsync("text", cts.Token));
    }
}
