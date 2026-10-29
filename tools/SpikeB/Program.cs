using System.Diagnostics;
using System.Globalization;
using System.IO;
using Path = System.IO.Path;
using System.Management;
using System.Text.Json;
using GlobalAutocorrect.Windows.Native;
using GlobalAutocorrect.Windows.Uia;
using GlobalAutocorrect.Windows.Write;
using Interop.UIAutomationClient;
using static GlobalAutocorrect.Windows.Native.NativeMethods;

namespace SpikeB;

/// <summary>
/// Spike B (2.4.b): in each target, type text as a user would, read the caret context with UIA, fix one word
/// with one tagged SendInput batch, then re-read and compare. Pass = no desync in at least 6 of 7.
/// </summary>
internal static class Program
{
    private const string Typed = "alpha bravo chralie delta echo";
    private const string Wrong = "chralie", Right = "charlie";

    private sealed record Target(string Name, Func<Launch> Start, Action<UiaReader, Launch>? Focus = null);
    private sealed record Launch(Func<IEnumerable<Process>> Processes, Action Close);
    private sealed record Result(string Target, string Status, string Source, string ControlType, string ReadBefore,
        string ReadAfterWrite, bool Verified, bool Desync, string Note);

    [STAThread]
    private static int Main(string[] args)
    {
        string repo = FindRepo();
        string pages = Path.Combine(repo, "tests", "pages");
        string profile = Path.Combine(Path.GetTempPath(), "ga-spikeb-edge");
        var only = args.Length > 0 ? new HashSet<string>(args, StringComparer.OrdinalIgnoreCase) : null;

        var targets = new List<Target>
        {
            new("Notepad", () => StartNew("notepad.exe", "", "Notepad")),
            new("Word", () => StartNew(OfficeExe("WINWORD.EXE"), "/q /w", "WINWORD")),
            new("Outlook", () => StartNew(OfficeExe("OUTLOOK.EXE"), "/c ipm.note", "OUTLOOK"), FocusOutlookBody),
        };
        foreach (var page in new[] { "textarea", "contenteditable", "prosemirror", "lexical" })
            targets.Add(new($"Edge {page}", () => StartEdge(profile, Path.Combine(pages, page + ".html"))));

        var results = new List<Result>();
        using var guard = new InputGuard();
        if (only?.Contains("--probe") == true)
        {
            Thread.Sleep(15_000);
            foreach (var kv in guard.Counts.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Key}: {kv.Value}");
            Console.WriteLine($"physical seen: {guard.PhysicalInputSeen}");
            return 0;
        }
        var reader = new UiaReader();
        foreach (var t in targets.Where(t => only is null || only.Contains(t.Name.Split(' ')[^1]) || only.Contains(t.Name)))
        {
            Result? r = null;
            for (int attempt = 1; attempt <= 5 && r is null; attempt++)
            {
                if (!WaitForIdle(TimeSpan.FromMinutes(2), TimeSpan.FromHours(8))) break;
                guard.Reset();
                r = RunOne(t, reader, guard);
                if (r is null) Console.WriteLine($"  {t.Name}: real input detected, retry {attempt}/5 after the next idle period (2.8)");
            }
            r ??= new Result(t.Name, "Skipped", "", "", "", "", false, false, "user was active");
            results.Add(r);
            Console.WriteLine($"{r.Target,-22} {(r.Verified ? "OK" : r.Desync ? "DESYNC" : "FAIL"),-7} {r.Status,-13} {r.Source,-13} {r.Note}");
        }

        int passed = results.Count(r => r.Verified && !r.Desync);
        Console.WriteLine($"\nSpike B: {passed}/{results.Count} without desync (pass bar 6/7)");
        Directory.CreateDirectory(Path.Combine(repo, "artifacts"));
        File.WriteAllText(Path.Combine(repo, "artifacts", "spikeB.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return passed >= 6 ? 0 : 1;
    }

    /// <summary>Returns null when real user input interrupted the run.</summary>
    private static Result? RunOne(Target t, UiaReader reader, InputGuard guard)
    {
        Launch? launch = null;
        try
        {
            launch = t.Start();
            var hwnd = WaitForWindow(launch, TimeSpan.FromSeconds(30));
            if (hwnd == 0) return Fail(t, "no window");
            BringToFront(hwnd);
            Thread.Sleep(2500);
            reader.Warm();   // 10.6.d
            Thread.Sleep(800);
            t.Focus?.Invoke(reader, launch);
            BringToFront(hwnd);
            Thread.Sleep(500);
            if (guard.PhysicalInputSeen) return null;

            var typer = new SendInputWriter(0);   // untagged: looks like the user typing
            foreach (char c in Typed)
            {
                typer.Type(c.ToString());
                Thread.Sleep(25);
            }
            Thread.Sleep(700);
            if (guard.PhysicalInputSeen) return null;

            var snap = ReadWithRetry(reader);
            if (!snap.CaretKnown) return new Result(t.Name, snap.Status.ToString(), snap.Source, snap.ControlType, "", "", false, false, snap.ProcessName);

            string before = snap.Before;
            int idx = before.LastIndexOf(Wrong, StringComparison.Ordinal);
            if (idx < 0) return new Result(t.Name, "NotTyped", snap.Source, snap.ControlType, Show(before), "", false, false, "typed text not found before caret");

            string tail = before[(idx + Wrong.Length)..];
            int caretDistance = new StringInfo(tail).LengthInTextElements;
            if (guard.PhysicalInputSeen) return null;
            bool sent = SendInputWriter.Tagged.ApplyEdit(caretDistance, Wrong.Length, Right);
            Thread.Sleep(600);

            var after = ReadWithRetry(reader);
            string expected = before[..idx] + Right + tail;
            bool beforeOk = Norm(after.Before).EndsWith(Norm(expected), StringComparison.Ordinal)
                            || Norm(expected).EndsWith(Norm(after.Before), StringComparison.Ordinal);
            bool afterOk = Norm(after.After) == Norm(snap.After);   // caret restored to the same place
            bool verified = sent && after.CaretKnown && beforeOk && afterOk;
            string note = !sent ? "SendInput refused" : !beforeOk ? "text mismatch" : !afterOk ? "caret moved" : "";
            return new Result(t.Name, snap.Status.ToString(), snap.Source, snap.ControlType, Show(before), Show(after.Before),
                verified, !verified && sent, note);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            return Fail(t, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { launch?.Close(); } catch (InvalidOperationException) { }
            Thread.Sleep(1500);
        }
    }

    /// <summary>2.8: only take over the screen after the user has left the keyboard and mouse alone for a while.</summary>
    private static bool WaitForIdle(TimeSpan idle, TimeSpan giveUp)
    {
        var sw = Stopwatch.StartNew();
        bool announced = false;
        while (sw.Elapsed < giveUp)
        {
            var info = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
            GetLastInputInfo(ref info);
            uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
            if (idleMs >= idle.TotalMilliseconds) return true;
            if (!announced) { Console.WriteLine($"  waiting for {idle.TotalMinutes:F0} min without keyboard/mouse use (2.8)…"); announced = true; }
            Thread.Sleep(5000);
        }
        return false;
    }

    private static Result Fail(Target t, string note) => new(t.Name, "Failed", "", "", "", "", false, false, note);

    private static UiaSnapshot ReadWithRetry(UiaReader reader)
    {
        UiaSnapshot s = reader.Read();
        for (int i = 0; i < 6 && !s.CaretKnown; i++)
        {
            Thread.Sleep(500);
            reader.Warm();
            s = reader.Read();
        }
        return s;
    }

    private static string Norm(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00A0', ' ').Replace("\u200B", "").TrimEnd('\n', ' ');
    private static string Show(string s) => Norm(s).Length > 60 ? "…" + Norm(s)[^60..] : Norm(s);

    // ---- launching -------------------------------------------------------------------------

    private static Launch StartNew(string exe, string args, string processName)
    {
        var existing = Process.GetProcessesByName(processName).Select(p => p.Id).ToHashSet();
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        IEnumerable<Process> Mine() => Process.GetProcessesByName(processName).Where(p => !existing.Contains(p.Id));
        // Only ever close processes this spike started; the user's own windows are left alone.
        return new Launch(Mine, () => { foreach (var p in Mine()) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } });
    }

    private static Launch StartEdge(string profile, string page)
    {
        string exe = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        string url = new Uri(page).AbsoluteUri;
        Process.Start(new ProcessStartInfo(exe,
            $"--user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --new-window \"{url}\"") { UseShellExecute = false });
        IEnumerable<Process> Mine()
        {
            using var q = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedge.exe'");
            foreach (ManagementObject o in q.Get())
            {
                if (((string?)o["CommandLine"])?.Contains(profile, StringComparison.OrdinalIgnoreCase) != true) continue;
                Process? p = null;
                try { p = Process.GetProcessById((int)(uint)o["ProcessId"]); } catch (ArgumentException) { }
                if (p is not null) yield return p;
            }
        }
        return new Launch(Mine, () => { foreach (var p in Mine()) { try { p.Kill(); } catch (InvalidOperationException) { } } });
    }

    private static string OfficeExe(string name) => Path.Combine(@"C:\Program Files\Microsoft Office\Root\Office16", name);

    private static nint WaitForWindow(Launch l, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            foreach (var p in l.Processes())
            {
                p.Refresh();
                if (p.MainWindowHandle != 0) return p.MainWindowHandle;
            }
            Thread.Sleep(300);
        }
        return 0;
    }

    private static void BringToFront(nint hwnd)
    {
        if (GetForegroundWindow() == hwnd) return;
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint me = GetCurrentThreadId();
        AttachThreadInput(me, fgThread, true);
        SetForegroundWindow(hwnd);
        AttachThreadInput(me, fgThread, false);
    }

    /// <summary>
    /// Classic Outlook opens the compose window (class rctrl_renwnd32) with focus in "To", and may also open its main
    /// window. Find the compose window among Outlook's top-level windows and move focus to its body (class _WwG).
    /// </summary>
    private static void FocusOutlookBody(UiaReader reader, Launch l)
    {
        var uia = reader.Automation;
        var pids = l.Processes().Select(p => p.Id).ToHashSet();
        for (int i = 0; i < 40; i++)
        {
            var windows = uia.GetRootElement().FindAll(TreeScope.TreeScope_Children, uia.CreateTrueCondition());
            for (int w = 0; w < windows.Length; w++)
            {
                var win = windows.GetElement(w);
                if (!pids.Contains(win.CurrentProcessId) || win.CurrentClassName != "rctrl_renwnd32") continue;
                var body = win.FindFirst(TreeScope.TreeScope_Descendants, uia.CreatePropertyCondition(30012, "_WwG"));   // ClassName
                if (body is null) continue;
                BringToFront(win.CurrentNativeWindowHandle);
                Thread.Sleep(300);
                body.SetFocus();
                return;
            }
            Thread.Sleep(500);
            pids = l.Processes().Select(p => p.Id).ToHashSet();
        }
    }

    /// <summary>The repo root, found from the exe's folder or, when run from a copy outside the repo, the working directory.</summary>
    private static string FindRepo()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = new DirectoryInfo(start);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "GlobalAutocorrect.slnx"))) d = d.Parent;
            if (d is not null) return d.FullName;
        }
        throw new InvalidOperationException("repo root not found; run from inside the repository");
    }
}
