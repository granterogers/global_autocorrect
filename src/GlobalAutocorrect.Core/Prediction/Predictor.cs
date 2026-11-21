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
    public double MinNextShare { get; set; } = 0.6;   // D80: 0.1 when "Always guess the next word" is on
    private const int ContextWindow = 400;

    /// <summary>
    /// D57: a word or phrase the user typed in the app they're typing in counts this many times as much as one typed in
    /// another app. 1 turns per-app learning off. 2 was the best of 1.5, 2, 3 and 5 on the tuning set (`eval predict
    /// --apps style`), and on the holdout it was neither better nor worse than off.
    /// </summary>
    public double CurrentAppWeight { get; init; } = 2;

    private static readonly Regex ContextToken = new(
        @"\p{L}+(?:['’]\p{L}+)*|[.!?]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>D142: the phrases the user repeats; null when phrase learning isn't set up.</summary>
    public PhraseMemory? Phrases { get; init; }

    /// <summary>
    /// D159: the model's next pieces of text after a prompt (with its own time limit), or null; null when there is no model.
    /// </summary>
    public Func<string, IReadOnlyList<Ai.TokenChance>?>? ModelNext { get; set; }

    /// <summary>Setting to turn prediction off (7.7).</summary>
    public bool Enabled { get; set; } = true;

    public Suggestion? Suggest(string textBeforeCaret) => Suggest(textBeforeCaret, null);

    /// <param name="app">D57: the focused app's process name, so its own history counts for more; null when unknown.</param>
    public Suggestion? Suggest(string textBeforeCaret, string? app)
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

        // D142: the rest of a phrase the user repeats, when what followed these words before agrees: the user's own way of
        // finishing it beats a one-word guess. Tab takes it all; typing on keeps it while the letters match.
        if (Phrases?.Continue(line) is { Length: > 0 } rest)
        {
            int sp = rest.IndexOf(' ');
            return new Suggestion(rest, sp > 0 ? rest[..sp] : rest, IsNextWord: char.IsWhiteSpace(line[^1]));
        }

        char last = line[^1];
        if (char.IsLetter(last))
        {
            return CompletePartial(line, app, textBeforeCaret);
        }

        if (last == ' ' && line.Length >= 2 && !char.IsWhiteSpace(line[^2]))
        {
            return PredictNext(line, app);
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

    /// <summary>D158: the candidate that scored <paramref name="runnerUp"/>, other than <paramref name="best"/>.</summary>
    private static string? RunnerUpWord(Dictionary<string, double> candidates, string best, double runnerUp) =>
        candidates.Where(c => c.Key != best && c.Value == runnerUp).Select(c => c.Key).Order(StringComparer.Ordinal).FirstOrDefault();

    private Suggestion? CompletePartial(string line, string? app, string? before = null)
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
            candidates[word] = Score(count, Mine(word, app));
        }

        foreach (var (word, h) in history.WordsStartingWith(lower))
        {
            if (!candidates.ContainsKey(word))
            {
                candidates[word] = Score(Math.Max(1, model.CountOf(word)), Mine(word, h, app));
            }
        }

        // D40: words the user has typed after these same words before come first ("how are you do" → "doing").
        var (c1, c2) = ContextBefore(line[..start]);
        var after = history.NextAfter(c1, c2, app, out bool sameTwo);
        // D57: as in Mine, the same say in all, leaning towards what was typed after these words in this app.
        double ps = after.Count > 0 ? after.Sum(a => (double)a.Count) / after.Sum(a => Blend(a.Count, a.InApp)) : 1;
        foreach (var (word, count, inApp) in after)
        {
            if (word.StartsWith(lower, StringComparison.Ordinal))
                candidates[word] = candidates.GetValueOrDefault(word, Score(Math.Max(1, model.CountOf(word)), Mine(word, app))) * (1 + (sameTwo ? 40.0 : 10.0) * ps * Blend(count, inApp));
        }

        // D159 (user: "If this is still an op" should be "option", not "open"): the model reads the sentence and says which
        // of the candidates comes next; a clear pick beats how common each word is on its own.
        if (before is not null && ModelNext is not null && Learning.Tunables.PredictUseModel.On
            && ModelPick(before, partial, lower, candidates) is { } picked)
            return Finish(partial, lower, picked);

        // D51: the word as typed competes. "before" was completed to "beforehand", "Leeds" to "Leeds's": when the typed
        // letters are already a word, ending it there is usually what happens, so a longer word must clearly beat it.
        long asTypedCount = model.CountOf(lower);
        double asTyped = asTypedCount > 0 ? Score(asTypedCount, Mine(lower, app)) : 0;

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

        // D158 (user: "If this is still an opt" offered nothing): a close call between words that share a longer start
        // which is a word itself ("options" or "option") offers that shared word: right in both cases so far.
        if (best is not null && runnerUp > 0 && bestScore < RunnerUpRatio * runnerUp && RunnerUpWord(candidates, best, runnerUp) is { } second)
        {
            int n = 0;
            while (n < best.Length && n < second.Length && best[n] == second[n]) n++;
            string shared = best[..n];
            if (shared.Length > lower.Length + 1 && model.CountOf(shared) > 0 && model.CountOf(shared) * 2 >= asTypedCount)
            {
                best = shared;
                runnerUp = 0;
            }
        }

        if (best is null || (runnerUp > 0 && bestScore < RunnerUpRatio * runnerUp) || bestScore < RunnerUpRatio * asTyped)
        {
            return null;
        }

        return Finish(partial, lower, best);
    }

    /// <summary>
    /// D159: the model's pick among the words that start with what was typed: each scored by the chance of the model's
    /// next piece of text that leads into it, the list topped up with words the model itself leads to. Null unless one
    /// stands out (<see cref="Learning.Tunables.PredictModelMinChance"/>, <see cref="Learning.Tunables.PredictModelLead"/>).
    /// </summary>
    private string? ModelPick(string before, string partial, string lower, Dictionary<string, double> candidates)
    {
        int keep = Learning.Tunables.PredictModelContextChars.Int;
        string prompt = before.Length > keep ? before[^keep..] : before;
        var next = ModelNext!(prompt);
        if (next is null || next.Count == 0) return null;
        var words = new HashSet<string>(candidates.Keys, StringComparer.Ordinal);
        foreach (var (word, _) in model.Complete(lower, 30)) words.Add(word);
        foreach (var t in next)
        {
            string piece = t.Token.ToLowerInvariant();
            int n = 0;
            while (n < piece.Length && (char.IsLetter(piece[n]) || piece[n] == '\'')) n++;
            if (n > 0 && model.CountOf(lower + piece[..n]) > 0) words.Add(lower + piece[..n]);
        }
        var chance = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var word in words)
        {
            if (word.Length <= lower.Length + 1 || !word.StartsWith(lower, StringComparison.Ordinal) || word == lower + "'s") continue;
            string rest = word[lower.Length..];
            double p = 0;
            foreach (var t in next)
            {
                string piece = t.Token.ToLowerInvariant();
                if (piece.Length == 0) continue;
                // The piece is the start of the rest of the word ("tion" for "option"), or the rest and then the word ends.
                bool leads = rest.StartsWith(piece, StringComparison.Ordinal)
                             || (piece.StartsWith(rest, StringComparison.Ordinal) && !char.IsLetter(piece[rest.Length]));
                if (leads) p = Math.Max(p, t.P);
            }
            if (p > 0) chance[word] = p;
        }
        if (chance.Count == 0) return null;
        double bestP = chance.Values.Max();
        // The same piece leads into several words ("tion": "option", "options"): to the model they are one choice. Among
        // them, the word they all start with if it is one of them, else the most common; the lead is over the rest.
        var group = chance.Where(c => c.Value >= bestP - 1e-9).Select(c => c.Key).ToList();
        double secondP = chance.Where(c => c.Value < bestP - 1e-9).Select(c => c.Value).DefaultIfEmpty(0).Max();
        string shared = group.Aggregate((a, b) => a[..Enumerable.Range(0, Math.Min(a.Length, b.Length)).TakeWhile(i => a[i] == b[i]).Count()]);
        string best = group.Contains(shared) ? shared : group.OrderByDescending(model.CountOf).ThenBy(w => w, StringComparer.Ordinal).First();
        return bestP >= Learning.Tunables.PredictModelMinChance.Value && bestP >= Learning.Tunables.PredictModelLead.Value * secondP ? best : null;
    }

    private static Suggestion Finish(string partial, string lower, string best)
    {
        var rest = best[lower.Length..];
        if (partial.Contains('’'))
        {
            rest = rest.Replace('\'', '’');
        }

        bool allCaps = partial.All(c => !char.IsLetter(c) || char.IsUpper(c));
        return new Suggestion(allCaps ? rest.ToUpperInvariant() : rest, partial + rest, IsNextWord: false);
    }

    private Suggestion? PredictNext(string line, string? app)
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
        var mine = history.NextAfter(w1, w2 ?? NgramModel.SentenceStart, app, out bool twoWords);
        if (next.Count == 0 && mine.Count == 0)
        {
            return null;
        }

        // D40: a share of the corpus continuations and a share of the user's own, weighted by how often the user has
        // typed this context: once is already two thirds of the say, so a phrase typed once is suggested the next time.
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        double corpusTotal = next.Sum(n => Score(n.Count, Mine(n.Word, app)));
        foreach (var (word, count) in next)
            scores[word] = Score(count, Mine(word, app)) / corpusTotal;
        long mineTotal = mine.Sum(m => m.Count);
        // D51: after the same two words, once is two thirds of the say (clears MinNextShare); after one word, half.
        double weight = mineTotal / (mineTotal + (twoWords ? 0.5 : 1.0));
        foreach (var k in scores.Keys.ToList()) scores[k] *= 1 - weight;
        // D57: the say is the same wherever it was typed; within it, what was typed in this app counts for more.
        double blendTotal = mine.Sum(m => Blend(m.Count, m.InApp));
        foreach (var (word, count, inApp) in mine)
            scores[word] = scores.GetValueOrDefault(word) + weight * Blend(count, inApp) / blendTotal;

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

    /// <summary>D57: a history count with the current app's share of it weighted by <see cref="CurrentAppWeight"/>.</summary>
    private double Blend(long count, long inApp) => count + (CurrentAppWeight - 1) * inApp;

    private double Mine(string word, string? app) => Mine(word, history.Count(word), app);

    /// <summary>
    /// D57: the user's count for a word, leaning towards the current app. Scaled so the user's words as a whole keep the
    /// same say against the corpus: per-app learning only changes which of them is preferred, never how often the
    /// user's history wins (boosting without that scaling showed more suggestions, fewer of them right).
    /// </summary>
    private double Mine(string word, long count, string? app)
    {
        if (app is null || CurrentAppWeight == 1) return count;
        var (all, inApp) = history.Totals(app);
        if (inApp == 0) return count;   // nothing typed here yet: the global history as it is
        return Blend(count, history.CountIn(app, word)) * all / Blend(all, inApp);
    }

    private static double Score(long corpusCount, double historyCount) =>
        corpusCount * (1.0 + 20.0 * historyCount / (historyCount + 50.0));
}
