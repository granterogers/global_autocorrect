using GlobalAutocorrect.Core.Text;

namespace GlobalAutocorrect.Core.Engine;

/// <summary>Timing and behaviour settings for Feature A (4.4, 4.6). All are user or tuning settings.</summary>
public sealed record EngineOptions
{
    /// <summary>4.4.a: AI pass after this pause. Setting range 300–2000 ms.</summary>
    public int PauseMs { get; init; } = 700;

    /// <summary>4.6.b: the last two words become fixable after this pause.</summary>
    public int ActiveTypingMs { get; init; } = 1500;

    /// <summary>4.6.c: an AI write needs this long without a key.</summary>
    public int WriteGapMs { get; init; } = 150;

    /// <summary>A queued write that can't go out within this time is dropped.</summary>
    public int WriteDeadlineMs { get; init; } = 6000;

    /// <summary>4.4.b: current sentence plus up to two before it.</summary>
    public int ContextSentences { get; init; } = 3;

    public bool InstantEnabled { get; init; } = true;
    public bool AiEnabled { get; init; } = true;

    public EditRuleSettings Rules { get; init; } = new();

    public EngineOptions Validate() => this with { PauseMs = Math.Clamp(PauseMs, 300, 2000) };
}
