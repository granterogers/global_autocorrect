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

    /// <summary>
    /// A queued write that can't go out within this time is dropped. D41: was 6 s, so a fast typist's fixes for a long
    /// reply expired before the next 150 ms gap (user report, Intercom). Staleness is caught anyway: the write is
    /// re-checked against the field and typed text just before it goes out (4.6.c) and verified after (4.6.d).
    /// </summary>
    public int WriteDeadlineMs { get; init; } = 60_000;

    /// <summary>D43: correct the whole current paragraph, not only what was typed since the last click or paste.</summary>
    public bool FixWholeParagraph { get; init; } = true;

    /// <summary>D39: how many line breaks typed since a word a fix may still cross to reach it.</summary>
    public int MaxLineBreaksCrossed { get; init; } = 8;

    /// <summary>4.4.b: current sentence plus up to two before it.</summary>
    public int ContextSentences { get; init; } = 3;

    public bool InstantEnabled { get; init; } = true;
    public bool AiEnabled { get; init; } = true;

    public EditRuleSettings Rules { get; init; } = new();

    public EngineOptions Validate() => this with { PauseMs = Math.Clamp(PauseMs, 300, 2000) };
}
