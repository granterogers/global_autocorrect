using System.IO;
using System.Text;
using GlobalAutocorrect.E2E.Support;
using GlobalAutocorrect.Windows.Write;
using Xunit;
using Path = System.IO.Path;

namespace GlobalAutocorrect.E2E;

/// <summary>
/// D105 probe, not a pass/fail test (run in Windows Sandbox with scripts/sandbox-e2e.ps1 -Filter ... -Name probe). The
/// real-typing test (D104) showed a fix 83 characters back, written with arrow keys during a pause, followed by the
/// user's next key landing 6 characters early in Edge. This sends the app's own edit batch (Left×d, Backspace, type,
/// Right×d) into a web editor, then one ordinary key press after a set delay, and records where that key went in and
/// the order the editor handled the events, for several distances and delays. No app under test: only the batch.
/// </summary>
[Trait("Category", "Probe")]
[Collection("screen")]
public sealed class WriteRaceProbe
{
    [Theory]
    [InlineData("contenteditable")]
    [InlineData("prosemirror")]
    [InlineData("textarea")]
    [InlineData("lexical")]
    [InlineData("ckeditor")]
    [InlineData("quill")]
    [InlineData("tinymce")]
    [InlineData("slate")]
    public async Task D105_HowLongAWebEditorTakesToFinishAnEditBatch(string editor)
    {
        if (!Desk.IsCi) Assert.Skip("Runs only in Windows Sandbox (CI=true): it types on the screen.");
        var (browser, page, pw) = await BrowserTests.Open(editor == "contenteditable" ? "probe" : editor);
        var report = new StringBuilder($"## {editor}\n\n| distance | delay ms | key landed | expected | ok |\n|---|---|---|---|---|\n");
        try
        {
            foreach (int distance in new[] { 10, 40, 80, 160 })
            {
                foreach (int delay in new[] { 0, 10, 30, 80, 150, 300 })
                {
                    // A fresh line of known text: "aaaa…" with one "Q" to fix, `distance` characters before the end.
                    Desk.Keys(0x11, 0x41); Desk.Keys(0x2E);   // Ctrl+A, Delete
                    string body = new string('a', 20) + "Q" + new string('b', distance);
                    new SendInputWriter(0).Type(body);
                    await Task.Delay(800, TestContext.Current.CancellationToken);
                    try { await page.EvaluateAsync("window.resetLog && window.resetLog()"); } catch { }
                    // The app's batch: step back over the tail and "Q", delete it, type "R", step forward again.
                    SendInputWriter.Tagged.ApplyEdit(distance, 1, "R");
                    if (delay > 0) Thread.Sleep(delay);
                    // The user's next key: a capital "X", Shift first, as a keyboard sends it (D100's race).
                    Desk.Key1(0x10, false); Thread.Sleep(40); Desk.Key1(0x58, false); Desk.Key1(0x58, true); Thread.Sleep(20); Desk.Key1(0x10, true);
                    await Task.Delay(1500, TestContext.Current.CancellationToken);
                    string got = (await page.EvaluateAsync<string>(RealTypingTests.ShownText)).Replace(' ', ' ').TrimEnd('\n');
                    string want = new string('a', 20) + "R" + new string('b', distance) + "X";
                    int at = got.IndexOf('X');
                    report.AppendLine($"| {distance} | {delay} | {(at < 0 ? "lost" : at - got.Length + 1 == 0 ? "end" : $"{got.Length - 1 - at} early")} | end | {(got == want ? "yes" : "NO: " + (got.Length > 60 ? "…" + got[^60..] : got))} |");
                    if (editor == "contenteditable" && got != want)
                    {
                        string[] log = await page.EvaluateAsync<string[]>("window.log");
                        report.AppendLine($"\n<details><summary>events, distance {distance}, delay {delay}</summary>\n\n```\n{string.Join("\n", log.TakeLast(30))}\n```\n</details>\n");
                    }
                }
            }
        }
        finally
        {
            string dir = Path.Combine(Desk.Repo, "artifacts", "probe");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "summary.md"), report + "\n");
            await browser.CloseAsync();
            pw.Dispose();
        }
    }

    /// <summary>
    /// D105: in Windows Sandbox the app saw the browser field's process as 0 ("Idle"), took it as elevated and left it
    /// alone. What UI Automation reports for the focused field there, and each Edge process's integrity level.
    /// </summary>
    [Fact]
    public async Task D105_WhatUiAutomationSeesInTheBrowser()
    {
        if (!Desk.IsCi) Assert.Skip("Runs only in Windows Sandbox (CI=true).");
        var (browser, page, pw) = await BrowserTests.Open("textarea");
        var report = new StringBuilder("## UI Automation in the browser\n\n");
        try
        {
            new SendInputWriter(0).Type("hello there");
            await Task.Delay(1000, TestContext.Current.CancellationToken);
            using var uia = new FlaUI.UIA3.UIA3Automation();
            for (int i = 0; i < 3; i++)
            {
                var el = uia.FocusedElement();
                int pid = -1; string name = "?", type = "?", cls = "?";
                try { pid = el.Properties.ProcessId.ValueOrDefault; } catch (Exception e) { name = "pid error " + e.GetType().Name; }
                try { type = el.ControlType.ToString(); cls = el.ClassName; } catch { }
                string proc = "?"; try { proc = System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch { }
                report.AppendLine($"- focused: pid={pid} ({proc}) type={type} class={cls} {name} textPattern={el.Patterns.Text.IsSupported}");
                await Task.Delay(500, TestContext.Current.CancellationToken);
            }
            report.AppendLine($"- this test: integrity 0x{GlobalAutocorrect.Windows.Targets.IntegrityLevel.Current:X}");
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("msedge"))
                report.AppendLine($"- msedge {p.Id}: integrity {(GlobalAutocorrect.Windows.Targets.IntegrityLevel.Of(p.Id) is { } l ? $"0x{l:X}" : "unreadable")}");
        }
        finally
        {
            string dir = Path.Combine(Desk.Repo, "artifacts", "probe");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "summary.md"), report + "\n");
            await browser.CloseAsync();
            pw.Dispose();
        }
    }

    /// <summary>The same in a WPF TextBox (a desktop field): where the real-typing test saw "os" fixed as "SO" and a line selected and deleted.</summary>
    [Fact]
    public void D105_HowAWpfBoxHandlesAShiftRightAfterAnEditBatch()
    {
        if (!Desk.IsCi) Assert.Skip("Runs only in Windows Sandbox (CI=true): it types on the screen.");
        using var box = new DesktopTests.WpfBox();
        var report = new StringBuilder("## wpf\n\n| distance | delay ms | ok |\n|---|---|---|\n");
        try
        {
            foreach (int distance in new[] { 10, 40, 80 })
            {
                foreach (int delay in new[] { 0, 10, 30, 80, 150 })
                {
                    Desk.Keys(0x11, 0x41); Desk.Keys(0x2E);
                    new SendInputWriter(0).Type(new string('a', 20) + "Q" + new string('b', distance));
                    Thread.Sleep(500);
                    SendInputWriter.Tagged.ApplyEdit(distance, 1, "R");
                    if (delay > 0) Thread.Sleep(delay);
                    Desk.Key1(0x10, false); Thread.Sleep(40); Desk.Key1(0x58, false); Desk.Key1(0x58, true); Thread.Sleep(20); Desk.Key1(0x10, true);
                    Thread.Sleep(800);
                    string got = box.Text, want = new string('a', 20) + "R" + new string('b', distance) + "X";
                    report.AppendLine($"| {distance} | {delay} | {(got == want ? "yes" : "NO: " + (got.Length > 60 ? "…" + got[^60..] : got))} |");
                }
            }
        }
        finally
        {
            string dir = Path.Combine(Desk.Repo, "artifacts", "probe");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "summary.md"), report + "\n");
        }
    }
}
