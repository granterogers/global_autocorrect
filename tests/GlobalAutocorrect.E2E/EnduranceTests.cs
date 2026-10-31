using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.UIA3;
using GlobalAutocorrect.E2E.Support;
using Xunit;

namespace GlobalAutocorrect.E2E;

/// <summary>
/// D72 (12.x, 11.3): hours of typing into Notepad with the real app (mock model, suggestions on), sampling the app's
/// memory, handles, threads and GDI/USER objects every minute, and checking the fixes still land. A slow leak only shows
/// after a working day, which no other test covers. Opt-in: GA_ENDURANCE_MINUTES (run by .github/workflows/endurance.yml
/// on a GitHub runner, never in a normal run).
/// </summary>
[Trait("Category", "Endurance")]
public sealed class EnduranceTests
{
    private static readonly string[] Paragraphs =
    [
        "we went their with teh dog after work. it was cold but nice. ",
        "please recieve the parcel for me, i will be back at six. thanks again. ",
        "the meeting went their way in the end. teh notes are on the shared drive. ",
        "can you check teh figures before we send them. they look a bit high to me. ",
    ];

    private static string Model(string s) => s.Replace("went their", "went there");

    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);   // 0 = GDI, 1 = USER

    private sealed record Sample(int Minute, long PrivateMb, long WorkingMb, int Handles, int Threads, uint Gdi, uint User, int Paragraphs, int Missed);

    [Fact]
    public void R12_D72_HoursOfTypingDoNotLeakOrSlowDown()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("GA_ENDURANCE_MINUTES"), out int minutes) || minutes <= 0)
            Assert.Skip("Endurance run: set GA_ENDURANCE_MINUTES (runs on a GitHub runner via endurance.yml).");
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm, prediction: true, label: "endurance");
        using var uia = new UIA3Automation();
        var (_, doc, close) = DesktopTests.OpenNotepad(uia);
        var samples = new List<Sample>();
        int paragraphs = 0, missed = 0;
        var clock = Stopwatch.StartNew();
        var nextSample = TimeSpan.Zero;
        try
        {
            while (clock.Elapsed < TimeSpan.FromMinutes(minutes))
            {
                if (clock.Elapsed >= nextSample)
                {
                    samples.Add(Take(app.ProcessId, (int)clock.Elapsed.TotalMinutes, paragraphs, missed));
                    nextSample += TimeSpan.FromMinutes(1);
                }
                Desk.Type(Paragraphs[paragraphs % Paragraphs.Length]);
                Desk.Keys(0x0D);   // Enter: a new paragraph
                Thread.Sleep(2000);   // the user pauses: AI pass and look-back
                paragraphs++;
                string text = DesktopTests.Text(doc);
                string last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                if (last.Contains(" teh ", StringComparison.Ordinal) || last.Contains("went their", StringComparison.Ordinal) || last.Contains("recieve", StringComparison.Ordinal))
                    missed++;
                // Keep the document small, as a user would start a new message: select all and delete.
                if (paragraphs % 20 == 0) { Desk.Keys(0x11, 0x41); Desk.Keys(0x2E); Thread.Sleep(500); }
                Assert.False(Process.GetProcessById(app.ProcessId).HasExited, $"the app exited after {clock.Elapsed}; {app.Diagnostics()}");
            }
            samples.Add(Take(app.ProcessId, (int)clock.Elapsed.TotalMinutes, paragraphs, missed));
        }
        finally
        {
            close();
            Report(samples, app);
        }

        // Steady state is after the first 10% (start-up, caches, JIT); compare it with the last quarter.
        var early = samples.Skip(Math.Max(1, samples.Count / 10)).Take(Math.Max(1, samples.Count / 4)).ToList();
        var late = samples.Skip(samples.Count * 3 / 4).ToList();
        double Median(IEnumerable<double> v) { var s = v.Order().ToArray(); return s[s.Length / 2]; }
        double memEarly = Median(early.Select(s => (double)s.PrivateMb)), memLate = Median(late.Select(s => (double)s.PrivateMb));
        double hEarly = Median(early.Select(s => (double)s.Handles)), hLate = Median(late.Select(s => (double)s.Handles));
        double gdiEarly = Median(early.Select(s => (double)s.Gdi)), gdiLate = Median(late.Select(s => (double)s.Gdi));
        double userEarly = Median(early.Select(s => (double)s.User)), userLate = Median(late.Select(s => (double)s.User));
        Assert.True(memLate <= memEarly * 1.25 + 40, $"private memory grew from {memEarly} MB to {memLate} MB");
        Assert.True(hLate <= hEarly * 1.25 + 100, $"handles grew from {hEarly} to {hLate}");
        Assert.True(gdiLate <= gdiEarly + 50, $"GDI objects grew from {gdiEarly} to {gdiLate}");
        Assert.True(userLate <= userEarly + 50, $"USER objects grew from {userEarly} to {userLate}");
        Assert.True(missed <= paragraphs / 20, $"{missed} of {paragraphs} paragraphs kept a typo");
        Assert.Equal(0, app.Desyncs);
    }

    private static Sample Take(int pid, int minute, int paragraphs, int missed)
    {
        using var p = Process.GetProcessById(pid);
        p.Refresh();
        return new Sample(minute, p.PrivateMemorySize64 >> 20, p.WorkingSet64 >> 20, p.HandleCount, p.Threads.Count,
            GetGuiResources(p.Handle, 0), GetGuiResources(p.Handle, 1), paragraphs, missed);
    }

    private static void Report(List<Sample> samples, AppUnderTest app)
    {
        string dir = Path.Combine(Desk.Repo, "artifacts", "endurance");
        Directory.CreateDirectory(dir);
        var csv = new StringBuilder("minute,private_mb,working_mb,handles,threads,gdi,user,paragraphs,missed\n");
        foreach (var s in samples) csv.AppendLine($"{s.Minute},{s.PrivateMb},{s.WorkingMb},{s.Handles},{s.Threads},{s.Gdi},{s.User},{s.Paragraphs},{s.Missed}");
        File.WriteAllText(Path.Combine(dir, "samples.csv"), csv.ToString());
        if (samples.Count == 0) return;
        var f = samples[0]; var l = samples[^1];
        var md = new StringBuilder("## Endurance run\n\n| | start | end |\n|---|---|---|\n");
        md.AppendLine($"| minutes | {f.Minute} | {l.Minute} |");
        md.AppendLine($"| private memory, MB | {f.PrivateMb} | {l.PrivateMb} |");
        md.AppendLine($"| working set, MB | {f.WorkingMb} | {l.WorkingMb} |");
        md.AppendLine($"| handles | {f.Handles} | {l.Handles} |");
        md.AppendLine($"| threads | {f.Threads} | {l.Threads} |");
        md.AppendLine($"| GDI / USER objects | {f.Gdi} / {f.User} | {l.Gdi} / {l.User} |");
        md.AppendLine($"| paragraphs typed / kept a typo | | {l.Paragraphs} / {l.Missed} |");
        md.AppendLine($"\nDesyncs: {app.Desyncs}. Every minute's sample is in samples.csv.");
        File.WriteAllText(Path.Combine(dir, "summary.md"), md.ToString());
    }
}
