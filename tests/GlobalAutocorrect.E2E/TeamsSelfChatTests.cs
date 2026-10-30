using System.Diagnostics;
using System.IO;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.E2E.Support;
using Xunit;
using static GlobalAutocorrect.Windows.Native.NativeMethods;
using Path = System.IO.Path;

namespace GlobalAutocorrect.E2E;

/// <summary>
/// The installed app with the real model, in the user's real Teams (CKEditor 5), in their chat with themselves.
/// Opt-in only (GA_REAL_TEAMS=1 and GA_TEAMS_SELF = the user's sign-in address); never part of a normal run. It types,
/// waits, reads the box back and clears it. It never presses Enter, so nothing is sent, and before every sentence it
/// checks the window is still the self-chat ("(You)" in the title), stopping without typing if not. No "@", "/" or ":"
/// (mentions, commands, emoji). The app runs with a temporary data folder, so nothing reaches the user's history.
/// </summary>
[Trait("Category", "RealApp")]
[Collection("screen")]
public sealed class TeamsSelfChatTests
{
    private static readonly (string Typed, string Meant)[] Cases =
    [
        ("I have a simpley scas that im working on that imayeb could be of interests to  you to shadow me on ?", "I have a simple case that I'm working on that maybe could be of interest to you to shadow me on?"),
        ("hey can you hav a look at teh ticket when you get a chnace", "Hey can you have a look at the ticket when you get a chance"),
        ("im going to be about 10 minuets late for the standup sorry", "I'm going to be about 10 minutes late for the standup sorry"),
        ("the customer siad the export keeps faling when they open the large drawign", "The customer said the export keeps failing when they open the large drawing"),
        ("can we move the call to tomorow morning instaed of this afternoon", "Can we move the call to tomorrow morning instead of this afternoon"),
        ("thanks for sorting that out so quickyl, really apreciate it", "Thanks for sorting that out so quickly, really appreciate it"),
        ("i'll send over the revised qoute once ive checked the numbers with finance", "I'll send over the revised quote once I've checked the numbers with finance"),
        ("does anyone know if the new relase fixes the licence isue on windows 11", "Does anyone know if the new release fixes the licence issue on Windows 11"),
        ("just a heads up the server will be donw for maintanance tonight from 8pm", "Just a heads up the server will be down for maintenance tonight from 8pm"),
        ("could you plese forward me the email from the suplier about the delay", "Could you please forward me the email from the supplier about the delay"),
        ("I thnik we should procede with the second option, its cheaper and simplier", "I think we should proceed with the second option, it's cheaper and simpler"),
        ("lets catch up after lunch, I have a few questoins about the integartion", "Let's catch up after lunch, I have a few questions about the integration"),
        ("the meeting notes are in the shared foler if you need them", "The meeting notes are in the shared folder if you need them"),
        ("sory I missed your mesage earlier, I was on a call with a cusotmer all morning", "Sorry I missed your message earlier, I was on a call with a customer all morning"),
        ("can you confrim whether the training session is still going ahead on thursday", "Can you confirm whether the training session is still going ahead on Thursday"),
        ("we recieved the parts this morning but two of them were damged in transit", "We received the parts this morning but two of them were damaged in transit"),
        ("is it ok if I take friday off next week, I have a dentist apointment", "Is it OK if I take Friday off next week, I have a dentist appointment"),
        ("the report looks good overal, just a couple of small changse on page three", "The report looks good overall, just a couple of small changes on page three"),
        ("please make sure everyone has updated thier timesheets before the end of the day", "Please make sure everyone has updated their timesheets before the end of the day"),
        ("Thanks, that all looks fine to me.", "Thanks, that all looks fine to me."),   // clean: nothing should change
    ];

    [Fact]
    public void RealTeams_SelfChat_TwentySentences()
    {
        string? self = Environment.GetEnvironmentVariable("GA_TEAMS_SELF");
        if (Environment.GetEnvironmentVariable("GA_REAL_TEAMS") != "1" || string.IsNullOrWhiteSpace(self))
            Assert.Skip("Real Teams test: set GA_REAL_TEAMS=1 and GA_TEAMS_SELF (types into your Teams self-chat, never sends).");
        Desk.RequireIdle();

        var speller = HunspellSpellChecker.FromAssets(Path.Combine(Desk.Repo, "assets"));
        int NonWords(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim(',', '.', '?', '!'))
            .Count(w => w.Length > 0 && w.All(c => char.IsLetter(c) || c == '\'') && !speller.IsValid(w) && !speller.IsValid(w.ToLowerInvariant()));

        using var app = new RealApp();
        Process.Start(new ProcessStartInfo($"msteams:/l/chat/0/0?users={Uri.EscapeDataString(self)}") { UseShellExecute = true });
        using var uia = new UIA3Automation();
        Window? win = null;
        Assert.True(Desk.WaitUntil(() =>
        {
            var p = Process.GetProcessesByName("ms-teams").FirstOrDefault(x => x.MainWindowHandle != 0);
            win = p is null ? null : uia.FromHandle(p.MainWindowHandle).AsWindow();
            return win?.Title.Contains("(You)", StringComparison.Ordinal) == true;
        }, 15_000), "Teams did not open the self-chat: nothing was typed");
        var box = win!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit).And(cf.ByName("Type a message")))?.AsTextBox()
                  ?? throw new InvalidOperationException("Teams message box not found: nothing was typed");
        // Teams must be in front before anything is clicked: a click on a covered window would land on whatever is on top.
        nint hwnd = win.Properties.NativeWindowHandle.Value;
        win.SetForeground();
        Desk.BringToFront(hwnd);
        Assert.True(Desk.WaitUntil(() => GetForegroundWindow() == hwnd, 5000), "Teams would not come to the front: nothing was typed");
        box.Focus();
        Thread.Sleep(300);
        box.Click();
        Thread.Sleep(1500);

        void Guard()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            bool teams = Process.GetProcessById((int)pid).ProcessName.Equals("ms-teams", StringComparison.OrdinalIgnoreCase);
            if (!teams || !win.Title.Contains("(You)", StringComparison.Ordinal))
                throw new InvalidOperationException("Not the Teams self-chat any more: stopped without typing");
        }
        string Read() => (box.Patterns.Text.Pattern.DocumentRange.GetText(-1) ?? "").Replace(' ', ' ').Trim();
        void Clear() { Guard(); Desk.Keys(VK_CONTROL, (ushort)'A'); Desk.Keys(VK_DELETE); Thread.Sleep(400); }

        var report = new StringBuilder();
        int typedNonWords = 0, leftNonWords = 0, exact = 0;
        try
        {
            Clear();
            foreach (var (typed, meant) in Cases)
            {
                Clear();
                Guard();
                Desk.Type(typed, msPerKey: 70);
                // Wait for the fixes: until the box has stopped changing for 3 s (at most 15 s).
                string last = Read(); var still = Stopwatch.StartNew(); var all = Stopwatch.StartNew();
                while (all.ElapsedMilliseconds < 15_000 && still.ElapsedMilliseconds < 3000)
                {
                    Thread.Sleep(250);
                    string now = Read();
                    if (now != last) { last = now; still.Restart(); }
                }
                string got = Read();
                int before = NonWords(typed), after = NonWords(got);
                typedNonWords += before; leftNonWords += after;
                if (string.Equals(got.TrimEnd('.', '?', ' '), meant.TrimEnd('.', '?', ' '), StringComparison.OrdinalIgnoreCase)) exact++;
                report.AppendLine($"typed:  {typed}").AppendLine($"result: {got}").AppendLine($"meant:  {meant}")
                      .AppendLine($"        misspellings {before} -> {after}").AppendLine();
            }
        }
        finally
        {
            try { Clear(); } catch (InvalidOperationException) { }
        }

        string summary = $"Teams self-chat, real model: {Cases.Length} sentences, misspelt words {typedNonWords} -> {leftNonWords} " +
                         $"({(typedNonWords == 0 ? 0 : 100.0 * (typedNonWords - leftNonWords) / typedNonWords):F0}% fixed), " +
                         $"matching the intended text (ignoring case and final punctuation) {exact}/{Cases.Length}. Counters: {app.Counters()}";
        string diag = app.Diagnostics();
        File.WriteAllText(Path.Combine(Desk.Repo, "artifacts", "teams-selfchat-results.txt"), summary + "\n\n" + report + "diagnostics.log:\n" + diag);
        TestContext.Current.SendDiagnosticMessage(summary);
    }

    /// <summary>The installed app with the real model; test typing counts as the user's (GA_E2E=1); temporary data folder.</summary>
    private sealed class RealApp : IDisposable
    {
        private readonly Process _proc;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ga-real-" + Guid.NewGuid().ToString("N")[..8]);
        private static readonly string Exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GlobalAutocorrect", "GlobalAutocorrect.exe");

        public RealApp()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "settings.json"), """{ "FirstRunDone": true, "StartWithWindows": false, "PredictionEnabled": false }""");
            Process.Start(Exe, "--exit")?.WaitForExit(5000);   // the user's own copy holds the single-instance lock
            Thread.Sleep(2000);
            var psi = new ProcessStartInfo(Exe) { UseShellExecute = false };
            psi.Environment["GA_E2E"] = "1";
            psi.Environment["GA_DATA_DIR"] = _dir;
            _proc = Process.Start(psi)!;
            Thread.Sleep(10_000);   // model load and warm-up
            Assert.False(_proc.HasExited, "the app did not start");
        }

        public string Counters() => File.Exists(Path.Combine(_dir, "counters.json")) ? File.ReadAllText(Path.Combine(_dir, "counters.json")) : "(none)";
        public string Diagnostics() => File.Exists(Path.Combine(_dir, "diagnostics.log")) ? File.ReadAllText(Path.Combine(_dir, "diagnostics.log")) : "(none)\n";

        public void Dispose()
        {
            try { Process.Start(Exe, "--exit")?.WaitForExit(5000); _proc.WaitForExit(10_000); } catch (InvalidOperationException) { }
            if (!_proc.HasExited) _proc.Kill(entireProcessTree: true);
            Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true });   // the user's own copy, back as it was
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
