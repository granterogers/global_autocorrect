namespace GlobalAutocorrect.Core.Ai;

/// <summary>The cleaned text and how long the round trip took (6.3.d).</summary>
public sealed record LlmResult(string Text, double LatencyMs);

/// <summary>Sends text to the local model and returns the full cleaned text (4.4.e). Loopback only (3.3, 10.5).</summary>
public interface ILlmClient
{
    Task<LlmResult> CleanAsync(string text, CancellationToken cancellationToken);
}
