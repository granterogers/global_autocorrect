namespace GlobalAutocorrect.Core.Text;

/// <summary>"No rewrite" thresholds (4.5). Settings, tuned only on the tuning set (4.5.e).</summary>
public sealed record EditRuleSettings
{
    public static readonly IReadOnlyList<string> DefaultFunctionWords =
    [
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "am", "to", "of", "and", "or", "in", "on", "at",
        "for", "with", "it", "that", "this", "i", "but", "as", "by", "do", "does", "did", "have", "has", "had", "not",
        "so", "if", "from", "up", "out",
    ];

    /// <summary>4.5.a: accept a substituted word when normalised edit distance is at most this.</summary>
    public double MaxNormalisedDistance { get; init; } = 0.5;

    /// <summary>4.5.d: reject the whole correction when more than this share of words would change.</summary>
    public double MaxChangedWordRatio { get; init; } = 0.5;

    /// <summary>
    /// D129, user report (WhatsApp: "… at 1630 (or later)? at Portillo?": "you're introducing extra question marks"): a
    /// question mark or exclamation mark the user didn't type is their tone and meaning, not a slip, so the AI may not add
    /// one. Brain knob Rules.AddQuestionMarks (1 allows it again).
    /// </summary>
    public bool AddQuestionMarks { get; init; }

    /// <summary>D27: accept one likely keyboard slip even when it fails the distance threshold (short words).</summary>
    public bool AcceptLikelyKeyboardSlips { get; init; } = true;

    /// <summary>
    /// D36: a word that isn't a word ("ovwared", "kreminderthat", a stray "t") can't be the user's own wording, so a fix
    /// of it is judged leniently and doesn't count towards 4.5.d's "more than half the words changed". Real words keep
    /// the strict rules.
    /// </summary>
    public bool LenientWithNonWords { get; init; } = true;

    /// <summary>D36: the distance allowed when fixing a non-word whose first letter is kept.</summary>
    public double MaxNonWordDistance { get; init; } = 0.65;

    /// <summary>4.5.b: words that may be inserted or deleted on their own.</summary>
    public IReadOnlyList<string> FunctionWords { get; init; } = DefaultFunctionWords;
}
