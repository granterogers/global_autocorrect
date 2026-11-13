using GlobalAutocorrect.Core.Text;

namespace GlobalAutocorrect.Core.Eval;

/// <summary>One mistake a person would call stupid, with what it was (the eval's own synthetic text, never the user's).</summary>
public sealed record StupidMistake(string Kind, string Detail);

/// <summary>
/// D132, user: "I wouldn't be comfortable releasing this because there's just too many stupid mistakes". Misses are one
/// thing; these are the mistakes that make a person stop trusting the app, judged on the real model's output for a
/// message: capitals nobody typed, a word repeated, a "?" or "!" from nowhere, a correct word changed, a name, number,
/// time or link changed, text lost or added. The real-use gate allows none of them.
/// </summary>
public static class StupidMistakes
{
    public static IReadOnlyList<StupidMistake> Find(string noisy, string clean, string output, IReadOnlyList<string>? keep = null)
    {
        var found = new List<StupidMistake>();
        int Marks(string s) => s.Count(c => c is '?' or '!');
        if (Marks(output) > Math.Max(Marks(noisy), Marks(clean)))
            found.Add(new("added-?!", $"{Marks(noisy)} typed, {Marks(output)} after"));

        var n = Scorer.Words(noisy);
        var c = Scorer.Words(clean);
        var o = Scorer.Words(output);
        static string Core(string w) => new(w.Where(char.IsLetterOrDigit).ToArray());
        static bool AllCaps(string w) { string l = new(w.Where(char.IsLetter).ToArray()); return l.Length >= 2 && l.All(char.IsUpper); }

        var typedCaps = n.Concat(c).Where(AllCaps).Select(Core).ToHashSet(StringComparer.Ordinal);
        foreach (var w in o.Where(AllCaps).Select(Core).Where(w => !typedCaps.Contains(w)).Distinct())
            found.Add(new("capitals", w));

        static HashSet<string> Doubles(IReadOnlyList<string> ws) =>
            Enumerable.Range(1, Math.Max(0, ws.Count - 1)).Where(i => Core(ws[i]).Length > 0 && string.Equals(Core(ws[i]), Core(ws[i - 1]), StringComparison.OrdinalIgnoreCase))
                .Select(i => Core(ws[i]).ToLowerInvariant()).ToHashSet();
        var allowedDoubles = Doubles(c);
        foreach (var d in Doubles(o).Where(d => !allowedDoubles.Contains(d) && !Doubles(n).Contains(d)))
            found.Add(new("repeated-word", d));

        foreach (var k in keep ?? [])
            if (!output.Contains(k, StringComparison.Ordinal)) found.Add(new("protected-changed", k));

        // A word that was already right, changed (a "false edit", 4.5): aligned the way the eval scores messages.
        var cn = SequenceAligner.Align(c, n, WordDiff.PairCost);
        var co = SequenceAligner.Align(c, o, WordDiff.PairCost);
        var rightBefore = new bool[c.Count];
        foreach (var u in cn) if (u.ACount == 1 && u.BCount == 1) rightBefore[u.AStart] = c[u.AStart] == n[u.BStart];
        foreach (var u in co)
        {
            if (u.ACount == 1 && u.BCount == 1 && rightBefore[u.AStart] && c[u.AStart] != o[u.BStart])
                found.Add(new("correct-word-changed", $"{c[u.AStart]} -> {o[u.BStart]}"));
            else if (u.ACount > u.BCount && Enumerable.Range(u.AStart, u.ACount).Any(i => rightBefore[i]))   // dropped, or run together
                found.Add(new("word-lost", string.Join(' ', c.Skip(u.AStart).Take(u.ACount))));
        }
        if (o.Count > Math.Max(n.Count, c.Count) + 1) found.Add(new("text-added", $"{n.Count} words typed, {o.Count} after"));
        return found;
    }
}
