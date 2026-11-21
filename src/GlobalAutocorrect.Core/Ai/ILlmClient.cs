namespace GlobalAutocorrect.Core.Ai;

/// <summary>The cleaned text and how long the round trip took (6.3.d).</summary>
public sealed record LlmResult(string Text, double LatencyMs);

/// <summary>Sends text to the local model and returns the full cleaned text (4.4.e). Loopback only (3.3, 10.5).</summary>
public interface ILlmClient
{
    Task<LlmResult> CleanAsync(string text, CancellationToken cancellationToken);

    /// <summary>
    /// D159 (user: suggestions should be "based on the context of what I've already typed"): the model's likeliest next
    /// pieces of text after <paramref name="prompt"/>, each with its chance, on the model's second slot so the clean-up's
    /// stays as it was. Null when the model can't say (not running, busy, a stand-in without it).
    /// </summary>
    Task<IReadOnlyList<TokenChance>?> NextAsync(string prompt, int top, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TokenChance>?>(null);
}

/// <summary>D159: one piece of text the model might write next, and its chance (0–1).</summary>
public sealed record TokenChance(string Token, double P);
