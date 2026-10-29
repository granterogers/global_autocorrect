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

    /// <summary>4.5.b: words that may be inserted or deleted on their own.</summary>
    public IReadOnlyList<string> FunctionWords { get; init; } = DefaultFunctionWords;
}
