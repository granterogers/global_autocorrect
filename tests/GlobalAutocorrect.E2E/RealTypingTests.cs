using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FlaUI.UIA3;
using GlobalAutocorrect.E2E.Support;
using GlobalAutocorrect.Windows.Write;
using Xunit;
using Path = System.IO.Path;

namespace GlobalAutocorrect.E2E;

/// <summary>
/// D104 (0.2, 4.1, 4.6.c–d, 11.3; user: "I want a programme that works according to spec"). The other screen tests type
/// one sentence, stop, and check. Real use is different: the user types on without stopping, with real Shift presses for
/// capitals and symbols, line after line, while fixes for earlier words go in behind the caret. Every delivery bug the
/// user found (D98–D101) happened only like that. This test types four tuning-set messages (6.1: never the holdout) that
/// way into each editor named in 4.1, with a model stand-in that gives the right answer for every word, so what is
/// measured is delivery alone: each word must end up either fixed or as typed, never damaged, with no desync.
/// Results go to artifacts/realtyping/summary.md.
/// </summary>
[Trait("Category", "E2E")]
[Collection("screen")]
public sealed class RealTypingTests
{
    /// <summary>The messages typed, and the right answer for each noisy word in them (the model stand-in's whole knowledge).</summary>
    private sealed record Script(IReadOnlyList<(string Noisy, string Clean)> Lines, IReadOnlyDictionary<string, string> Fixes);

    private static readonly Lazy<Script> Messages = new(Pick);

    /// <summary>
    /// Tuning-set messages with typos and the same number of words before and after (so each word can be checked in place),
    /// 60–150 characters, and no word that is a typo in one message and correct in another. Fixed choice, every run.
    /// </summary>
    private static Script Pick()
    {
        var lines = new List<(string, string)>();
        var fixes = new Dictionary<string, string>(StringComparer.Ordinal);
        var correct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var json in File.ReadLines(Path.Combine(Desk.Repo, "data", "eval", "tuning.jsonl")))
        {
            using var doc = JsonDocument.Parse(json);
            string noisy = doc.RootElement.GetProperty("noisy").GetString()!, clean = doc.RootElement.GetProperty("clean").GetString()!;
            string[] n = noisy.Split(' '), c = clean.Split(' ');
            if (noisy == clean || n.Length != c.Length || noisy.Length is < 60 or > 150 || n.Any(w => w.Length == 0)) continue;
            // Only typos inside words (or a space moved by one), so each typed word lines up with the word it should be:
            // "att end training day n" has the right word count for "attend the training day in" but doesn't line up.
            var ops = doc.RootElement.GetProperty("ops").EnumerateArray().Select(o => o.GetString()).ToList();
            if (ops.Any(o => o is not ("neighbour" or "double" or "swap" or "drop" or "misplace" or "apostrophe" or "lowercase"))) continue;
            if (n.Zip(c).Any(p => p.First != p.Second && Distance(p.First, p.Second) > 2)) continue;
            var mine = new Dictionary<string, string>(StringComparer.Ordinal);
            bool clash = false;
            for (int i = 0; i < n.Length && !clash; i++)
            {
                if (n[i] == c[i]) clash = fixes.ContainsKey(n[i]) || mine.ContainsKey(n[i]);
                else clash = correct.Contains(n[i]) || (fixes.TryGetValue(n[i], out var f) && f != c[i]) || (mine.TryGetValue(n[i], out var m) && m != c[i]);
                if (!clash && n[i] != c[i]) mine[n[i]] = c[i];
            }
            if (clash || mine.Count < 2) continue;
            foreach (var (k, v) in mine) fixes[k] = v;
            for (int i = 0; i < n.Length; i++) if (n[i] == c[i]) correct.Add(n[i]);
            lines.Add((noisy, clean));
            if (lines.Count == 4) break;
        }
        Assert.Equal(4, lines.Count);
        return new Script(lines, fixes);
    }

    /// <summary>Levenshtein distance, case-insensitive (capitals are a fix of their own, not a misalignment).</summary>
    private static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>The model stand-in: every word it knows the answer for is fixed, the rest kept, spacing and lines as sent.</summary>
    private static string Oracle(string text)
    {
        var fixes = Messages.Value.Fixes;
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] is ' ' or '\n' or '\r') { sb.Append(text[i++]); continue; }
            int j = i;
            while (j < text.Length && text[j] is not (' ' or '\n' or '\r')) j++;
            string word = text[i..j];
            sb.Append(fixes.TryGetValue(word, out var f) ? f : word);
            i = j;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Every editor in 4.1, or (D108) only those in GA_RT_EDITORS, comma-separated, each run GA_RT_REPEAT times: a run
    /// is named "lexical-2", so its logs are kept apart.
    /// </summary>
    public static TheoryData<string> Editors
    {
        get
        {
            string[] all = ["notepad", "wpf", "textarea", "contenteditable", "prosemirror", "lexical", "ckeditor", "quill", "tinymce", "slate", "intercom", "email", "chat", "teams"];   // D126: "teams": CKEditor in WebView2, as new Teams   // D113: Intercom and Claude/ChatGPT stand-ins; D122: Intercom email, signature below
            var only = Environment.GetEnvironmentVariable("GA_RT_EDITORS")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int repeat = int.TryParse(Environment.GetEnvironmentVariable("GA_RT_REPEAT"), out int r) && r > 1 ? r : 1;
            var data = new TheoryData<string>();
            foreach (var e in only is { Length: > 0 } ? only : all)
                for (int i = 1; i <= repeat; i++) data.Add(repeat > 1 ? $"{e}-{i}" : e);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Editors))]
    public async Task R4_1_D104_TypingOnWithoutStoppingEveryFixLandsAndNothingIsDamaged(string run)
    {
        Desk.RequireIdle();
        string editor = run.Contains('-') && int.TryParse(run[(run.LastIndexOf('-') + 1)..], out _) ? run[..run.LastIndexOf('-')] : run;
        using var llm = new MockLlm(Oracle);
        using var app = new AppUnderTest(llm, label: $"realtyping-{run}");
        var script = Messages.Value;
        // D126: each repeat of an editor is typed with its own seed, so a nightly run of several repeats covers many rhythms.
        int seed = (run.Contains('-') && int.TryParse(run[(run.LastIndexOf('-') + 1)..], out int n) ? n : 1)
                   + (int.TryParse(Environment.GetEnvironmentVariable("GA_RT_SEED"), out int slot) ? slot * 100 : 0);   // nightly: each slot its own
        string text;
        if (editor == "teams")
        {
            using var box = new WebViewBox("ckeditor");
            TypeLikeAPerson(script, seed);
            text = Settle(() => box.Text);
        }
        else if (editor == "wpf")
        {
            using var box = new DesktopTests.WpfBox();
            TypeLikeAPerson(script, seed);
            text = Settle(() => box.Text);
        }
        else if (editor == "notepad")
        {
            // Windows Sandbox has no Notepad; the WPF box stands in for a plain desktop field there.
            if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"))) Assert.Skip("No Notepad here (Windows Sandbox).");
            using var uia = new UIA3Automation();
            var (_, doc, close) = DesktopTests.OpenNotepad(uia);
            try
            {
                TypeLikeAPerson(script, seed);
                text = Settle(() => DesktopTests.Text(doc));
            }
            finally { close(); }
        }
        else
        {
            var (browser, page, pw) = await BrowserTests.Open(editor);
            try
            {
                TypeLikeAPerson(script, seed);
                text = Settle(() => page.EvaluateAsync<string>(ShownText).GetAwaiter().GetResult());
            }
            finally
            {
                await browser.CloseAsync();
                pw.Dispose();
            }
        }

        long desyncs = app.Desyncs;   // 11.3.d: before the text
        var score = Score(script, text);
        string verifyFails = VerifyFailures(app);
        Report(run, score, desyncs, verifyFails);
        // D109: CKEditor must be retyped, so fixes on an earlier line are skipped there by design (a miss, never damage).
        // D117: in the chat box Enter sends the message, so a fix that wasn't in before it was sent can't be made (a miss).
        int allowedMisses = editor is "chat" ? score.NeedFix / 2 : score.NeedFix / 20;   // D125: CKEditor (Teams) is placed now, no longer retyped
        Assert.True(desyncs == 0 && score.Damaged.Count == 0 && score.Missed <= allowedMisses,
            $"{editor}: fixed {score.Fixed}/{score.NeedFix}, missed {score.Missed}, damaged {score.Damaged.Count} " +
            $"[{string.Join("; ", score.Damaged)}], desyncs {desyncs}, verify failures {verifyFails}; {app.Diagnostics()}");
    }

    /// <summary>
    /// The text as the editor shows it, line breaks included. Some pages' getText() joins paragraphs with nothing between
    /// them (ProseMirror's textContent), which made four lines read as one.
    /// </summary>
    internal const string ShownText = """
        () => {
          if (window.sent) return window.getText();   // D113: chat: the messages sent, then the box
          if (document.querySelector('#ed_ifr')) return window.getText();   // TinyMCE first: it hides an empty textarea
          const t = document.querySelector('textarea'); if (t) return t.value;
          const e = document.querySelector('.ProseMirror, .ck-editor__editable, .ql-editor, [data-slate-editor], [contenteditable=true]');
          return e ? e.innerText : window.getText();
        }
        """;

    private sealed record Result(int NeedFix, int Fixed, int Missed, List<string> Damaged);

    /// <summary>
    /// Each line against its message, word by word: the right answer (fixed), as typed (missed, when it needed a fix), or
    /// anything else (damaged: a fix landed in the wrong place, letters lost or doubled, a line joined or split).
    /// </summary>
    private static Result Score(Script script, string text)
    {
        // An editor's own smart quotes (ProseMirror turns ' into ’) are the editor's doing, not damage.
        var got = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ').Replace('’', '\'').Replace('‘', '\'')
            .Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        int need = 0, fixedCount = 0, missed = 0;
        var damaged = new List<string>();
        if (got.Count != script.Lines.Count) damaged.Add($"{got.Count} lines instead of {script.Lines.Count}");
        for (int l = 0; l < script.Lines.Count; l++)
        {
            string[] n = script.Lines[l].Noisy.Split(' '), c = script.Lines[l].Clean.Split(' ');
            string[] g = l < got.Count ? got[l].Split(' ') : [];
            need += n.Zip(c).Count(p => p.First != p.Second);
            if (g.Length != c.Length) { damaged.Add($"line {l + 1}: {g.Length} words instead of {c.Length}: \"{(l < got.Count ? got[l] : "")}\""); continue; }
            for (int i = 0; i < c.Length; i++)
            {
                if (g[i] == c[i]) { if (n[i] != c[i]) fixedCount++; }
                else if (g[i] == n[i]) missed++;
                else damaged.Add($"line {l + 1} word {i + 1}: \"{g[i]}\" (typed \"{n[i]}\", meant \"{c[i]}\")");
            }
        }
        return new Result(need, fixedCount, missed, damaged);
    }

    /// <summary>Waits for the fixes to finish: the text unchanged for 3 s, at most 30 s.</summary>
    private static string Settle(Func<string> read)
    {
        string last = read();
        var still = Stopwatch.StartNew();
        var total = Stopwatch.StartNew();
        while (still.Elapsed < TimeSpan.FromSeconds(3) && total.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(250);
            string now = read();
            if (now != last) { last = now; still.Restart(); }
        }
        return last;
    }

    private static string VerifyFailures(AppUnderTest app)
    {
        string log = Path.Combine(app.DataDir, "diagnostics.log");
        if (!File.Exists(log)) return "0";
        var lines = File.ReadLines(log).Where(l => l.Contains(" verify failed:", StringComparison.Ordinal)).ToList();
        int repaired = File.ReadLines(log).Count(l => l.Contains("caret-repaired", StringComparison.Ordinal));
        return repaired > 0 ? $"{lines.Count} ({repaired} caret repaired)" : lines.Count.ToString();
    }

    private static void Report(string editor, Result s, long desyncs, string verifyFails)
    {
        string dir = Path.Combine(Desk.Repo, "artifacts", "realtyping");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "summary.md");
        if (!File.Exists(path))
            File.WriteAllText(path, "| editor | fixed | missed | damaged | desyncs | verify failures |\n|---|---|---|---|---|---|\n");
        File.AppendAllText(path, $"| {editor} | {s.Fixed}/{s.NeedFix} | {s.Missed} | {s.Damaged.Count} | {desyncs} | {verifyFails} |\n");
        if (s.Damaged.Count > 0) File.AppendAllText(Path.Combine(dir, $"{editor}-damage.txt"), string.Join("\n", s.Damaged) + "\n");
    }

    // ---- typing like a person --------------------------------------------------------------------------------------

    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanExW(char ch, nint hkl);

    /// <summary>
    /// Real key presses, as the user's keyboard sends them: Shift goes down before a capital or symbol and comes up after
    /// it (D100's race), keys 45–140 ms apart, a short think now and then at a word end, Enter between messages, and no
    /// long pause until the end. The same seed gives the same rhythm every run.
    /// </summary>
    /// <summary>
    /// D126 (GA_RT_CHAOS=1, the nightly run): habits from real use that leave the text as it was, so each word is still
    /// scored in place: a wrong letter taken back with Backspace, a line break typed by mistake right after a capital
    /// (Shift still down when Enter went, WhatsApp, D124) and deleted again, a pause of 1.6–3 s mid-message (long fixes
    /// then go in while the user starts again), and bursts of fast keys.
    /// </summary>
    private static readonly bool Chaos = Environment.GetEnvironmentVariable("GA_RT_CHAOS") == "1";

    private static void Tap(ushort vk, Random rnd)
    {
        Press(vk, up: false);
        Thread.Sleep(rnd.Next(15, 45));
        Press(vk, up: true);
        Thread.Sleep(rnd.Next(45, 140));
    }

    private static void TypeLikeAPerson(Script script, int seed)
    {
        var rnd = new Random(seed);
        var habits = new Random(seed * 7919);   // its own stream, so seed 1 without chaos types exactly as before
        nint hkl = GetKeyboardLayout(0);
        int burst = 0;
        for (int l = 0; l < script.Lines.Count; l++)
        {
            foreach (char ch in script.Lines[l].Noisy)
            {
                if (Chaos && char.IsLower(ch) && habits.Next(25) == 0)
                {
                    char wrong = ch == 'z' ? 'x' : (char)(ch + 1);   // the key beside it, more or less
                    Tap((ushort)(VkKeyScanExW(wrong, hkl) & 0xFF), rnd);
                    Tap(0x08, rnd);   // Backspace
                }
                if (Chaos && burst == 0 && habits.Next(60) == 0) burst = habits.Next(6, 16);
                short scan = VkKeyScanExW(ch, hkl);
                int mods = scan >> 8;
                if (scan == -1 || (mods & ~1) != 0) new SendInputWriter(0).Type(ch.ToString());   // not on the keyboard: as text
                else
                {
                    ushort vk = (ushort)(scan & 0xFF);
                    bool shift = (mods & 1) != 0;
                    if (shift) { Press(0x10, up: false); Thread.Sleep(rnd.Next(20, 70)); }
                    Press(vk, up: false);
                    Thread.Sleep(rnd.Next(15, 45));
                    Press(vk, up: true);
                    if (shift && Chaos && char.IsUpper(ch) && habits.Next(6) == 0)
                    {
                        Tap(0x0D, rnd);   // Enter with Shift still down: a line break, not a send
                        Press(0x10, up: true);
                        Tap(0x08, rnd);   // seen and taken back
                    }
                    else if (shift) { Thread.Sleep(rnd.Next(5, 50)); Press(0x10, up: true); }
                }
                if (burst > 0) { burst--; Thread.Sleep(rnd.Next(15, 40)); }
                else Thread.Sleep(rnd.Next(45, 140));
                if (ch == ' ' && rnd.Next(10) == 0) Thread.Sleep(rnd.Next(300, 900));   // a short think, never 1.5 s (4.6.b)
                if (Chaos && ch == ' ' && habits.Next(14) == 0) Thread.Sleep(habits.Next(1600, 3000));   // a real pause mid-message
            }
            if (l < script.Lines.Count - 1)
            {
                Press(0x0D, up: false);
                Thread.Sleep(rnd.Next(15, 45));
                Press(0x0D, up: true);
                Thread.Sleep(rnd.Next(150, 400));
            }
        }
    }

    private static void Press(ushort vk, bool up) => Desk.Key1(vk, up);
}
