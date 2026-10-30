using System.Text.RegularExpressions;

namespace GlobalAutocorrect.Core.Prediction;

/// <summary>
/// What to show after the caret. <see cref="Completion"/> is exactly the text to insert after the caret;
/// <see cref="Word"/> is the whole predicted word.
/// </summary>
public sealed record Suggestion(string Completion, string Word, bool IsNextWord);

/// <summary>
/// Word prediction (7.1): completes the partial word being typed, or after a space predicts the next word.
/// Candidates come from the corpus n-gram model, boosted by the user's own word counts (7.3) and, more strongly, by the
/// short phrases the user has typed before (D40), so their own way of putting things is what gets suggested.
/// </summary>
public sealed class Predictor(NgramModel model, UserHistory history)
{
    /// <summary>Letters typed before a completion is offered (D51).</summary>
    public int MinPartialLength { get; init; } = 2;
    private const int CompleteCandidates = 8;
    private const int NextCandidates = 5;
    /// <summary>A completion is shown only when it scores this many times the runner-up (D51: tuned with `eval predict`).</summary>
    public double RunnerUpRatio { get; init; } = 2.5;

    /// <summary>A next word is shown only when it has at least this share of the candidates' score (D51).</summary>
    public double MinNextShare { get; init; } = 0.6;
    private const int ContextWindow = 400;

    private static readonly Regex ContextToken = new(
        @"\p{L}+(?:['’]\p{L}+)*|[.!?]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Setting to turn prediction off (7.7).</summary>
    public bool Enabled { get; set; } = true;

    public Suggestion? Suggest(string textBeforeCaret)
    {
        if (!Enabled || string.IsNullOrEmpty(textBeforeCaret))
        {
            return null;
        }

        int nl = textBeforeCaret.LastIndexOfAny(['\n', '\r']);
        var line = nl >= 0 ? textBeforeCaret[(nl + 1)..] : textBeforeCaret;
        var trimmed = line.TrimEnd();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var token = trimmed[(trimmed.LastIndexOfAny([' ', '\t']) + 1)..];
        if (IsBlocked(token))
        {
            return null;
        }

        char last = line[^1];
        if (char.IsLetter(last))
        {
            return CompletePartial(line);
        }

        if (last == ' ' && line.Length >= 2 && !char.IsWhiteSpace(line[^2]))
        {
            return PredictNext(line);
        }

        return null;
    }

    private static bool IsBlocked(string token)
    {
        foreach (char c in token)
        {
            if (char.IsDigit(c) || c == '@')
            {
                return true;
            }
        }

        return token.Contains("://", StringComparison.Ordinal)
            || token.Contains("www.", StringComparison.OrdinalIgnoreCase);
    }

    private Suggestion? CompletePartial(string line)
    {
        int start = line.Length;
        while (start > 0 && (char.IsLetter(line[start - 1]) || line[start - 1] is '\'' or '’'))
        {
            start--;
        }

        while (start < line.Length && line[start] is '\'' or '’')
        {
            start++;
        }

        var partial = line[start..];
        if (partial.Length < MinPartialLength)
        {
            return null;
        }

        var lower = partial.Replace('’', '\'').ToLowerInvariant();

        var candidates = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (word, count) in model.Complete(lower, CompleteCandidates))
        {
            candidates[word] = Score(count, history.Count(word));
        }

        foreach (var (word, h) in history.WordsStartingWith(lower))
        {
            if (!candidates.ContainsKey(word))
            {
                candidates[word] = Score(Math.Max(1, model.CountOf(word)), h);
            }
        }

        // D40: words the user has typed after these same words before come first ("how are you do" → "doing").
        var (c1, c2) = ContextBefore(line[..start]);
        foreach (var (word, count) in history.NextAfter(c1, c2, out bool sameTwo))
        {
            if (word.StartsWith(lower, StringComparison.Ordinal))
                candidates[word] = candidates.GetValueOrDefault(word, Score(Math.Max(1, model.CountOf(word)), history.Count(word))) * (1 + (sameTwo ? 40.0 : 10.0) * count);
        }

        // D51: the word as typed competes. "before" was completed to "beforehand", "Leeds" to "Leeds's": when the typed
        // letters are already a word, ending it there is usually what happens, so a longer word must clearly beat it.
        long asTypedCount = model.CountOf(lower);
        double asTyped = asTypedCount > 0 ? Score(asTypedCount, history.Count(lower)) : 0;

        string? best = null;
        double bestScore = 0, runnerUp = 0;
        foreach (var (word, score) in candidates)
        {
            if (word.Length <= lower.Length + 1 || word == lower + "'s")   // too short to help, or a bare possessive
            {
                continue;
            }

            if (score > bestScore || (score == bestScore && best is not null && string.CompareOrdinal(word, best) < 0))
            {
                runnerUp = bestScore;
                bestScore = score;
                best = word;
            }
            else if (score > runnerUp)
            {
                runnerUp = score;
            }
        }

        if (best is null || (runnerUp > 0 && bestScore < RunnerUpRatio * runnerUp) || bestScore < RunnerUpRatio * asTyped)
        {
            return null;
        }

        var rest = best[lower.Length..];
        if (partial.Contains('’'))
        {
            rest = rest.Replace('\'', '’');
        }

        bool allCaps = partial.All(c => !char.IsLetter(c) || char.IsUpper(c));
        return new Suggestion(allCaps ? rest.ToUpperInvariant() : rest, partial + rest, IsNextWord: false);
    }

    private Suggestion? PredictNext(string line)
    {
        var body = line[..^1];
        char before = body[^1];
        bool sentenceStart = before is '.' or '!' or '?';
        if (!sentenceStart && !char.IsLetter(before))
        {
            return null;
        }

        bool truncated = body.Length > ContextWindow;
        var window = truncated ? body[^ContextWindow..] : body;
        var matches = ContextToken.Matches(window);
        if (matches.Count == 0)
        {
            return null;
        }

        string? w1, w2;
        if (sentenceStart)
        {
            w1 = null;
            w2 = NgramModel.SentenceStart;
        }
        else
        {
            var m2 = matches[^1];
            if (m2.Index + m2.Length != window.Length)
            {
                return null;
            }

            w2 = m2.Value.Replace('’', '\'').ToLowerInvariant();
            if (matches.Count >= 2)
            {
                var m1 = matches[^2];
                w1 = m1.Value is "." or "!" or "?"
                    ? NgramModel.SentenceStart
                    : m1.Value.Replace('’', '\'').ToLowerInvariant();
            }
            else
            {
                w1 = truncated ? null : NgramModel.SentenceStart;
            }
        }

        var next = model.Next(w1, w2, NextCandidates);
        var mine = history.NextAfter(w1, w2 ?? NgramModel.SentenceStart, out bool twoWords);
        if (next.Count == 0 && mine.Count == 0)
        {
            return null;
        }

        // D40: a share of the corpus continuations and a share of the user's own, weighted by how often the user has
        // typed this context: once is already two thirds of the say, so a phrase typed once is suggested the next time.
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        double corpusTotal = next.Sum(n => Score(n.Count, history.Count(n.Word)));
        foreach (var (word, count) in next)
            scores[word] = Score(count, history.Count(word)) / corpusTotal;
        long mineTotal = mine.Sum(m => m.Count);
        // D51: after the same two words, once is two thirds of the say (clears MinNextShare); after one word, half.
        double weight = mineTotal / (mineTotal + (twoWords ? 0.5 : 1.0));
        foreach (var k in scores.Keys.ToList()) scores[k] *= 1 - weight;
        foreach (var (word, count) in mine)
            scores[word] = scores.GetValueOrDefault(word) + weight * count / mineTotal;

        string? best = null;
        double bestScore = 0, total = 0;
        foreach (var (word, score) in scores)
        {
            total += score;
            if (score > bestScore)
            {
                bestScore = score;
                best = word;
            }
        }

        if (best is null || best.Length < 2 || bestScore / total < MinNextShare)
        {
            return null;
        }

        var completion = sentenceStart ? char.ToUpperInvariant(best[0]) + best[1..] : best;
        return new Suggestion(completion, best, IsNextWord: true);
    }

    /// <summary>The two words before a point in the line, for the user's phrases (D40); a sentence start is "&lt;s&gt;".</summary>
    private static (string? W1, string? W2) ContextBefore(string text)
    {
        var m = ContextToken.Matches(text.Length > ContextWindow ? text[^ContextWindow..] : text);
        static string W(Match x) => x.Value is "." or "!" or "?" ? NgramModel.SentenceStart : x.Value.Replace('’', '\'').ToLowerInvariant();
        if (m.Count == 0) return (null, NgramModel.SentenceStart);
        string w2 = W(m[^1]);
        if (w2 == NgramModel.SentenceStart) return (null, w2);
        return (m.Count >= 2 ? W(m[^2]) : NgramModel.SentenceStart, w2);
    }

    private static double Score(long corpusCount, long historyCount) =>
        corpusCount * (1.0 + 20.0 * historyCount / (historyCount + 50.0));
}
