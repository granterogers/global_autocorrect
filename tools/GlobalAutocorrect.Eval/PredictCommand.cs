using System.Text.Json;
using GlobalAutocorrect.Core.Prediction;

namespace GlobalAutocorrect.Eval;

/// <summary>
/// `predict --set holdout|tuning [--learn-from tuning]`: word prediction scored by simulated typing (D51). At each
/// character the predictor is asked for a suggestion; if what comes next starts with it, the typist presses Tab. Reports
/// keystrokes saved, how often a suggestion was shown, and how often a shown suggestion was right. With --learn-from, the
/// history first learns from those messages (as if the user had typed them), and keeps learning as it goes.
/// D57: `--apps style` types each message in a pretend app chosen by its style in messages.jsonl (chat in Teams, email
/// in Outlook, notes in Notepad; the hand-garbled ones in an unknown app), so the history learns per app; `--per-app off`
/// then predicts without telling the predictor which app it is in, from the same history.
/// </summary>
internal static class PredictCommand
{
    public static int Run(Dictionary<string, string> o, string repo)
    {
        string set = o.GetValueOrDefault("set", "holdout");
        string assets = Path.Combine(repo, "assets");
        string historyPath = Path.Combine(Path.GetTempPath(), "ga-predict-" + Guid.NewGuid().ToString("N") + ".txt");
        var history = new UserHistory(historyPath);
        var predictor = new Predictor(NgramModel.FromAssets(assets), history)
        {
            RunnerUpRatio = double.Parse(o.GetValueOrDefault("runner-up", "2.5"), System.Globalization.CultureInfo.InvariantCulture),
            MinNextShare = double.Parse(o.GetValueOrDefault("min-share", "0.6"), System.Globalization.CultureInfo.InvariantCulture),
            MinPartialLength = int.Parse(o.GetValueOrDefault("min-letters", "2")),
            CurrentAppWeight = double.Parse(o.GetValueOrDefault("app-weight", "2"), System.Globalization.CultureInfo.InvariantCulture),
        };
        var styles = o.GetValueOrDefault("apps") == "style"
            ? File.ReadLines(Path.Combine(repo, "data", "eval", "messages.jsonl")).Where(l => l.Trim().Length > 0).Select(l => JsonDocument.Parse(l).RootElement)
                .ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("style").GetString() switch
                {
                    "chat" => "ms-teams",
                    "email" => "olk",
                    "notes" => "notepad",
                    var s => s,
                })
            : null;
        bool perApp = o.GetValueOrDefault("per-app", "on") != "off";
        (string Text, string? App)[] Load(string name) => File.ReadLines(Path.Combine(repo, "data", "eval", name + ".jsonl")).Where(l => l.Trim().Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Select(e => (e.GetProperty("clean").GetString()!, styles?.GetValueOrDefault(e.GetProperty("id").GetString()!))).ToArray();
        bool learn = o.TryGetValue("learn-from", out var from);
        if (learn && from != "none") foreach (var (m, app) in Load(from!)) Learn(history, m, app);   // "none": learn only as it goes

        long chars = 0, keys = 0, shown = 0, right = 0; int examples = 0;
        var kinds = new Dictionary<string, (long Shown, long Right)>();
        void Tally(string k, bool ok) { var v = kinds.GetValueOrDefault(k); kinds[k] = (v.Shown + 1, v.Right + (ok ? 1 : 0)); }
        foreach (var (m, app) in Load(set))
        {
            int i = 0;
            while (i < m.Length)
            {
                var s = predictor.Suggest(m[..i], perApp ? app : null);
                if (s is not null && s.Completion.Length > 0)
                {
                    shown++;
                    bool ok = m.AsSpan(i).StartsWith(s.Completion, StringComparison.Ordinal);
                    int typedPart = s.Word.Length - s.Completion.Length;
                    Tally(s.IsNextWord ? "next word" : $"complete after {Math.Min(typedPart, 5)}{(typedPart >= 5 ? "+" : "")} letters", ok);
                    if (!ok && o.ContainsKey("examples") && examples++ < 40 && (s.IsNextWord || typedPart >= 5))
                    {
                        int we = i; while (we < m.Length && (char.IsLetter(m[we]) || m[we] == '\'')) we++;
                        Console.WriteLine($"   {(s.IsNextWord ? "next" : "word")}: typed \"{m[Math.Max(0, i - 25)..i]}\" suggested \"{s.Completion}\" actual \"{m[i..Math.Min(m.Length, we + 1)]}\"");
                    }
                    if (ok)
                    {
                        right++;
                        keys++;                       // Tab
                        i += s.Completion.Length;
                        continue;
                    }
                }
                keys++;
                i++;
            }
            chars += m.Length;
            if (learn) Learn(history, m, app);
        }
        Console.WriteLine($"{set}{(learn ? " (learning from " + from + ")" : "")}{(styles is null ? "" : perApp ? " (apps by style, per-app on)" : " (apps by style, per-app off)")}: keystrokes saved {1 - (double)keys / chars:P1}, " +
                          $"suggestions shown {shown}, right {(shown == 0 ? 0 : (double)right / shown):P1}, accepted {right}");
        if (o.ContainsKey("detail"))
            foreach (var (k, v) in kinds.OrderBy(k => k.Key)) Console.WriteLine($"   {k,-26} shown {v.Shown,5}  right {(double)v.Right / v.Shown:P0}");
        try { File.Delete(historyPath); } catch (IOException) { }
        return 0;
    }

    /// <summary>What the engine teaches the history as each word is finished (7.3, D40).</summary>
    private static void Learn(UserHistory h, string message, string? app)
    {
        string? w1 = null, w2 = UserHistory.SentenceStart;
        foreach (System.Text.RegularExpressions.Match t in System.Text.RegularExpressions.Regex.Matches(message, @"\p{L}+(?:['’]\p{L}+)*|[.!?]"))
        {
            if (t.Value is "." or "!" or "?") { (w1, w2) = (null, UserHistory.SentenceStart); continue; }
            h.Observe(t.Value, app);
            h.ObservePhrase(w1, w2, t.Value, app);
            (w1, w2) = (w2, t.Value.ToLowerInvariant());
        }
    }
}
