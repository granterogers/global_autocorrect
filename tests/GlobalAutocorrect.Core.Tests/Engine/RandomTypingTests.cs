using System.Text;
using System.Text.RegularExpressions;
using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Prediction;
using GlobalAutocorrect.Core.Session;
using GlobalAutocorrect.Core.Simulation;
using GlobalAutocorrect.Core.Telemetry;
using GlobalAutocorrect.Core.Tests.Instant;
using GlobalAutocorrect.Core.Tests.Simulated;
using GlobalAutocorrect.Core.Write;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>
/// D61: thousands of random typing sessions against the real engine, the real dictionary and suggester, and a simulated
/// field with every real-editor quirk (D42) switched on at random. Random typing has no "right answer" to compare with,
/// so instead every write is checked, as it lands, against rules it must never break:
///  1. it deletes exactly the text it meant to replace (plus, when retyping, a tail it types back unchanged);
///  2. an instant word fix starts and ends on a word boundary in the field (D60: never a fragment);
///  3. the check after writing never fails (no desyncs) and the engine never throws.
/// GA_FUZZ_SESSIONS runs more sessions than the default (e.g. 5000 for a long local run).
/// </summary>
public class RandomTypingTests
{
    private static readonly string[] Words =
    [
        "the", "and", "with", "team", "looking", "forward", "to", "hearing", "from", "you", "please", "let", "me", "know",
        "if", "there", "their", "they", "would", "could", "should", "about", "update", "thanks", "again", "regards",
        "customer", "support", "issue", "problem", "settings", "working", "properly", "tomorrow", "morning", "meeting",
        "software", "remotely", "popup", "close", "next", "steps", "services", "liaise", "afraid", "being", "time",
        "kitchen", "quote", "sure", "last", "paragraph", "store", "other", "day", "bus", "train", "better", "easier",
        "I", "I'm", "it's", "don't", "we", "our", "this", "that", "what", "when", "which", "because", "really", "quite",
    ];

    public static IEnumerable<object[]> Seeds()
    {
        int n = int.TryParse(Environment.GetEnvironmentVariable("GA_FUZZ_SESSIONS"), out var v) ? v : 300;
        for (int s = 1; s <= n; s++) yield return [s];
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void R4_6_D61_RandomTypingNeverDamagesTheField(int seed)
    {
        var run = new Session(seed);
        run.Play();
        Assert.True(run.Problems.Count == 0, $"seed {seed} [{run.Quirks}]: {string.Join(" | ", run.Problems.Take(3))}\nops: {run.Ops}\nfinal: \"{run.Ed.Text}\"");
    }

    private sealed class Session
    {
        private readonly Random _r;
        private readonly ManualClock _clock = new(10_000);
        private readonly Dictionary<string, string> _typos = new(StringComparer.OrdinalIgnoreCase);
        private readonly CorrectionEngine _engine;
        private readonly Counters _counters = new();
        public SimulatedEditor Ed { get; }
        public List<string> Problems { get; } = [];
        private readonly StringBuilder _ops = new();
        public string Ops => _ops.ToString();
        public string Quirks => string.Join(",", new[] { Ed.RetypeInsteadOfArrows ? "retype" : null, Ed.ReportsBlankLinesOnce ? "blank" : null,
            Ed.ReportsDoubleSpaceAsNbsp ? "nbsp" : null, Ed.GhostAfterCaret.Length > 0 ? "ghost" : null, Ed.LagChance > 0 ? "lag" : null }.Where(q => q is not null));

        private static readonly Lazy<SymSpellSuggester> Suggester = new(() => SymSpellSuggester.FromAssets(HunspellTests.Assets()));
        private static readonly Lazy<NgramModel> Ngram = new(() => NgramModel.FromAssets(HunspellTests.Assets()));

        public Session(int seed)
        {
            _r = new Random(seed);
            Ed = new SimulatedEditor(_clock)
            {
                Chance = new Random(seed * 7919),
                RetypeInsteadOfArrows = _r.NextDouble() < 0.3,           // CKEditor (Teams)
                ReportsBlankLinesOnce = _r.NextDouble() < 0.4,            // Edge rich editors
                ReportsDoubleSpaceAsNbsp = _r.NextDouble() < 0.4,
                GhostAfterCaret = _r.NextDouble() < 0.3 ? " suggestion" : "",
                GhostChance = 0.3,
                LagChance = _r.NextDouble() < 0.3 ? 0.2 : 0,
            };
            // The scripted model fixes the typos this session made, whole words only, and changes nothing else.
            var llm = new FakeLlm(Fix, latencyMs: 150 + _r.Next(400));
            var deps = new EngineDeps(_clock, Ed, HunspellTests.Shared, Suggester.Value, llm, _ => false, _ => false, _counters,
                PairCount: Ngram.Value.PairCount, WordCount: Ngram.Value.CountOf);
            _engine = new CorrectionEngine(new EngineOptions(), deps);
            _engine.SetTarget(Ed);
            _engine.Corrected += Check;
            _engine.Session.Restarted += r => _ops.Append($"<{r}>");
            _engine.Corrected += n => _ops.Append($"<{n.Layer}:{Show(n.Original)}→{Show(n.Replacement)}>");
            _engine.Desynced += _ => Problems.Add(Ed.LastEdit is { } e
                ? $"desync after deleting [{Show(e.Deleted)}] and typing [{Show(e.Inserted)}] in \"{Around(e.TextBefore, e.Start, e.Start + e.Deleted.Length)}\""
                : "desync");
        }

        private string Fix(string text) => Regex.Replace(text, @"[\p{L}']+", m =>
            _typos.TryGetValue(m.Value, out var good) ? InstantCorrector.MatchCase(m.Value, good) : m.Value);

        public void Play()
        {
            try
            {
                int steps = 30 + _r.Next(60);
                for (int i = 0; i < steps; i++) Step();
                Wait(9000);   // the user stops: AI passes, look-back and sweeps all get their turn
                if (_counters.Desyncs > 0) Problems.Add($"{_counters.Desyncs} desync(s)");
            }
            catch (Exception e) { Problems.Add($"threw {e.GetType().Name} at {e.StackTrace?.Split('\n')[0].Trim()}"); }
        }

        private void Step()
        {
            double p = _r.NextDouble();
            if (p < 0.55) TypeWord();
            else if (p < 0.65) Key(_r.Next(3) switch { 0 => ". ", 1 => ", ", _ => "? " });
            else if (p < 0.70) Key(_r.Next(2) == 0 ? " ." : "  ");                         // spacing slips (D55)
            else if (p < 0.75) Key(_r.Next(3) == 0 ? "\n\n" : "\n");
            else if (p < 0.82) { int n = 1 + _r.Next(3); _ops.Append($"<bs{n}>"); for (int i = 0; i < n; i++) Send(Ed.Backspace()); }
            else if (p < 0.87)
            {
                // A click somewhere else in the field, often in the middle of a word, then typing there (D60).
                int at = _r.Next(Ed.Text.Length + 1);
                _ops.Append($"<click@{at}>");
                Ed.Caret = at;
                Send(Ed.MoveCaret(0));
            }
            else if (p < 0.90) { int d = _r.Next(-6, 3); _ops.Append($"<arrow{d}>"); Send(Ed.MoveCaret(d)); }
            else if (p < 0.93) { string s = string.Join(' ', Enumerable.Range(0, 3).Select(_ => Words[_r.Next(Words.Length)])) + " "; _ops.Append($"<paste:{s}>"); Send(Ed.Paste(s)); }
            else Wait(_r.Next(2) == 0 ? 200 + _r.Next(600) : 800 + _r.Next(2500));      // a pause: AI and look-back
        }

        private void TypeWord()
        {
            string w = Words[_r.Next(Words.Length)];
            string typed = _r.NextDouble() < 0.35 ? Typo(w) : w;
            if (typed != w && !HunspellTests.Shared.IsValid(typed)) _typos[typed] = w;
            Key(typed + (_r.NextDouble() < 0.85 ? " " : ""));
        }

        private string Typo(string w)
        {
            if (w.Length < 3) return w;
            int i = 1 + _r.Next(w.Length - 2);
            return _r.Next(4) switch
            {
                0 => w[..i] + w[i + 1] + w[i] + w[(i + 2)..],          // swap
                1 => w.Remove(i, 1),                                    // dropped letter
                2 => w.Insert(i, w[i].ToString()),                      // doubled letter
                _ => w[..i] + "qwertyuiopasdfghjklzxcvbnm"[_r.Next(26)] + w[(i + 1)..],   // wrong key
            };
        }

        private void Key(string s)
        {
            _ops.Append(s.Replace("\n", "⏎"));
            foreach (char c in s)
            {
                int gap = _r.NextDouble() < 0.8 ? 30 + _r.Next(90) : 150 + _r.Next(400);   // mostly fast, sometimes slow
                for (; gap > 25; gap -= 25) { _clock.Advance(25); _engine.Tick(); }
                _clock.Advance(gap);
                Send(Ed.TypeChar(c));
            }
        }

        private void Send(KeyInput k) { _engine.OnInput(k); _engine.Tick(); }

        private void Wait(int ms) { for (int t = 0; t < ms; t += 25) { _clock.Advance(25); _engine.Tick(); } }

        /// <summary>Rules 1 and 2, checked against what the field saw at the moment of the write.</summary>
        private void Check(CorrectionNotice n)
        {
            if (Ed.LastEdit is not { } e) { Problems.Add("a write was reported with no edit in the field"); return; }
            string tail = n.Retyped ? e.Deleted[Math.Min(n.Original.Length, e.Deleted.Length)..] : "";
            if (e.Deleted != n.Original + tail || e.Inserted != n.Replacement + tail)
                Problems.Add($"{n.Layer} write deleted [{Show(e.Deleted)}] typed [{Show(e.Inserted)}], meant [{Show(n.Original)}] -> [{Show(n.Replacement)}]");
            // A capital at a sentence start or for "i" (D55) changes one letter inside a word on purpose: not a word fix.
            bool capital = n.Original.Length == 1 && n.Replacement == n.Original.ToUpperInvariant();
            bool wordFix = n.Layer == Layer.Instant && !capital && n.Original.Length > 0 && n.Original.All(char.IsLetter);
            if (wordFix)
            {
                int end = e.Start + n.Original.Length;
                bool startOk = e.Start == 0 || !char.IsLetterOrDigit(e.TextBefore[e.Start - 1]);
                bool endOk = end >= e.TextBefore.Length || !char.IsLetterOrDigit(e.TextBefore[end]);
                if (!startOk || !endOk)
                    Problems.Add($"instant fix of a fragment: [{Show(n.Original)}] -> [{Show(n.Replacement)}] inside \"{Around(e.TextBefore, e.Start, end)}\"");
            }
        }

        private static string Show(string s) => s.Replace("\n", "⏎");
        private static string Around(string t, int s, int e) => Show(t[Math.Max(0, s - 8)..Math.Min(t.Length, e + 8)]);
    }
}
