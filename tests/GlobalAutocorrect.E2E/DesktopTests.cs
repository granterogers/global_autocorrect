using System.IO;
using Path = System.IO.Path;
using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using GlobalAutocorrect.E2E.Support;
using Xunit;
using static GlobalAutocorrect.Windows.Native.NativeMethods;

namespace GlobalAutocorrect.E2E;

/// <summary>Feature A end to end in desktop apps (11.3.a). Real typing, real hook, real UIA and SendInput; mock model.</summary>
[Trait("Category", "E2E")]
[Collection("screen")]   // one at a time: they share the keyboard
public sealed class DesktopTests
{
    // Inputs avoid what Notepad's own autocorrect fixes ("teh", a lone "i"): when both fix the same word the app
    // correctly sees the text change under it and backs off (11.3.d), which says nothing about our fix.
    private static string Model(string s) => s.Replace("We went their", "We went there");

    /// <summary>
    /// Opens a fresh Notepad tab and, at the end, empties and closes it, so Notepad's session restore never keeps
    /// test text (Notepad autosaves tabs; 12.1 inputs avoid words Notepad corrects itself).
    /// </summary>
    internal static (Window? Window, TextBox Doc, Action Close) OpenNotepad(UIA3Automation uia)
    {
        var before = Process.GetProcessesByName("Notepad").Select(p => p.Id).ToHashSet();
        // Classic (Win32) Notepad, as on Windows Server CI runners: one document per window, no tabs, no autosave, and
        // closing an edited document asks to save, so the test's own copy is simply ended. Store Notepad runs from
        // WindowsApps and hands off to its existing process.
        List<Process> NewClassic() => Process.GetProcessesByName("Notepad").Where(p => !before.Contains(p.Id) && IsClassic(p)).ToList();
        void EndNewClassic()
        {
            foreach (var p in NewClassic()) { try { p.Kill(); p.WaitForExit(3000); } catch (InvalidOperationException) { } }
            Thread.Sleep(300);
        }
        Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        // Store Notepad hands off to another process, so wait for its window to be in front rather than attaching.
        var sw = Stopwatch.StartNew();
        bool inFront = Desk.WaitUntil(() =>
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            try { if (Process.GetProcessById((int)pid).ProcessName.Equals("Notepad", StringComparison.OrdinalIgnoreCase)) return true; }
            catch (ArgumentException) { }
            // Windows' focus rules can keep a new window behind the current one; after a while, hand it the foreground.
            if (sw.ElapsedMilliseconds > 3000)
                foreach (var p in NewClassic()) { p.Refresh(); if (p.MainWindowHandle != 0) Desk.BringToFront(p.MainWindowHandle); }
            return false;
        }, 10_000);
        if (!inFront) { EndNewClassic(); Assert.Fail("Notepad did not come to the front"); }
        Thread.Sleep(800);
        GetWindowThreadProcessId(GetForegroundWindow(), out uint fgPid);
        bool classic;
        try { classic = IsClassic(Process.GetProcessById((int)fgPid)); }
        catch (ArgumentException) { classic = false; }
        Window? win = null;
        Desk.Keys(VK_CONTROL, (ushort)'N');   // a new, empty tab even if Notepad restored others
        Thread.Sleep(800);
        // The editor of the new tab is whatever has focus now (class names vary between Notepad builds).
        var doc = (Desk.WaitUntil(() => uia.FocusedElement()?.Patterns.Text.IsSupported == true, 5000) ? uia.FocusedElement() : null)?.AsTextBox();
        if (doc is null)
        {
            EndNewClassic();
            throw new InvalidOperationException("Notepad editor not focused");
        }
        void Close()
        {
            if (classic) { EndNewClassic(); return; }
            doc.Focus();
            Desk.Keys(VK_CONTROL, (ushort)'A');
            Desk.Keys(VK_DELETE);
            Desk.Keys(VK_CONTROL, (ushort)'W');   // empty tab closes without a prompt
            Thread.Sleep(500);
            foreach (var p in Process.GetProcessesByName("Notepad").Where(p => !before.Contains(p.Id))) p.CloseMainWindow();
        }
        return (win, doc, Close);
    }

    private static bool IsClassic(Process p)
    {
        try { return p.MainModule?.FileName.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) == false; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, System.Text.StringBuilder lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    /// <summary>
    /// The field's text. Classic Notepad's Win32 Edit is read with WM_GETTEXT: on Windows Server 2025 its UIA text
    /// (the Win32 edit proxy) was seen to lag behind what the window showed after a correction.
    /// </summary>
    internal static string Text(TextBox doc)
    {
        string raw;
        if (doc.ClassName == "Edit" && doc.Properties.NativeWindowHandle.ValueOrDefault is var h && h != 0)
        {
            int len = (int)SendMessage(h, 0x000E, 0, 0);   // WM_GETTEXTLENGTH
            var sb = new System.Text.StringBuilder(len + 1);
            SendMessage(h, 0x000D, sb.Capacity, sb);        // WM_GETTEXT
            raw = sb.ToString();
        }
        else raw = doc.Patterns.Text.Pattern.DocumentRange.GetText(-1);
        return raw.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
    }

    [Fact]
    public void R11_3_a_NotepadInstantFix_R0_2_a()
    {
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm);
        using var uia = new UIA3Automation();
        var (_, doc, close) = OpenNotepad(uia);
        try
        {
            // Notepad's own autocorrect (when on) often fixes a misspelling first; then the app reads the corrected
            // field and leaves it alone. Either way the text must end right with no desync. Our own instant fix is
            // proved in the WPF box and the browser editors, which have no autocorrect.
            Desk.Type("we saw the bookk ");
            Desk.WaitUntil(() => Text(doc) == "we saw the book ", 1500);
            // 4.7 recorded check: the fading accent underline, captured while it is still visible.
            Directory.CreateDirectory(Path.Combine(Desk.Repo, "artifacts", "screens"));
            FlaUI.Core.Capturing.Capture.Element(doc).ToFile(Path.Combine(Desk.Repo, "artifacts", "screens", "e2e-underline.png"));
            Assert.Equal(0, app.Desyncs);   // 11.3.d
            Assert.True(Text(doc) == "we saw the book ", $"got \"{Text(doc)}\"; {app.Diagnostics()}");
        }
        finally { close(); }
    }

    [Fact]
    public void R11_3_a_NotepadAiFix_R0_2_b()
    {
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm);
        using var uia = new UIA3Automation();
        var (_, doc, close) = OpenNotepad(uia);
        try
        {
            Desk.Type("We went their yesterday. ");
            Desk.WaitUntil(() => Text(doc).StartsWith("We went there", StringComparison.Ordinal), 5000);
            Assert.Equal(0, app.Desyncs);
            Assert.True(Text(doc) == "We went there yesterday. ", $"got \"{Text(doc)}\"; {app.Diagnostics()}");
            Assert.True(llm.Requests >= 1);
        }
        finally { close(); }
    }

    [Fact]
    public void R7_4_TabAcceptsTheSuggestion_R7_1_R7_6()
    {
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm, prediction: true);
        using var uia = new UIA3Automation();
        var (_, doc, close) = OpenNotepad(uia);
        try
        {
            Desk.Type("Please let me ");
            Thread.Sleep(900);   // the suggestion appears after a background read
            Desk.Keys(VK_TAB);
            Assert.True(Desk.WaitUntil(() => Text(doc) == "Please let me know"), $"got \"{Text(doc)}\"; {app.Diagnostics()}");
            Assert.Equal(0, app.Desyncs);
        }
        finally { close(); }
    }

    [Fact]
    public void R7_4_TabPassesThroughWhenNothingIsShown()
    {
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm, prediction: true);
        using var uia = new UIA3Automation();
        var (_, doc, close) = OpenNotepad(uia);
        try
        {
            Desk.Type("zq");   // nothing to suggest
            Thread.Sleep(900);
            Desk.Keys(VK_TAB);
            Assert.True(Desk.WaitUntil(() => Text(doc) == "zq\t"), $"got \"{Text(doc)}\"; {app.Diagnostics()}");
        }
        finally { close(); }
    }

    private static HashSet<int> OurServers() =>
        Process.GetProcessesByName("llama-server")
            .Where(p => { try { return p.MainModule?.FileName.Contains(@"\GlobalAutocorrect\cache\", StringComparison.OrdinalIgnoreCase) == true; } catch (System.ComponentModel.Win32Exception) { return false; } })
            .Select(p => p.Id).ToHashSet();

    [Fact]
    public void R5_6_NoModelServerIsLeftRunningAfterTheAppCloses()
    {
        // Found when E2E runs left 15 llama-server processes filling the GPU.
        Desk.RequireIdle();
        var before = OurServers();
        using var llm = new MockLlm(Model);
        using (var app = new AppUnderTest(llm))
        using (var uia = new UIA3Automation())
        {
            var (_, doc, close) = OpenNotepad(uia);
            try { Desk.Type("a few words to type here "); Thread.Sleep(1500); }
            finally { close(); }
        }
        Thread.Sleep(2000);
        Assert.Empty(OurServers().Except(before));
    }

    /// <summary>
    /// A plain WPF TextBox in a window of the test process: a desktop field with no autocorrect of its own, so what it
    /// shows is only what the user typed and what the app changed.
    /// </summary>
    private sealed class WpfBox : IDisposable
    {
        private readonly System.Windows.Window _window;
        private readonly System.Windows.Controls.TextBox _box;

        public WpfBox()
        {
            System.Windows.Window? window = null;
            System.Windows.Controls.TextBox? box = null;
            using var ready = new ManualResetEventSlim();
            var ui = new Thread(() =>
            {
                box = new System.Windows.Controls.TextBox { FontSize = 16, AcceptsReturn = true };
                window = new System.Windows.Window { Title = "GA E2E WPF", Width = 600, Height = 200, Content = box, Topmost = true };
                window.Loaded += (_, _) => ready.Set();
                window.Show();
                System.Windows.Threading.Dispatcher.Run();
            });
            ui.SetApartmentState(ApartmentState.STA);
            ui.IsBackground = true;   // a failed test must not keep the test host alive
            ui.Start();
            Assert.True(ready.Wait(10_000, TestContext.Current.CancellationToken), "the WPF window did not open");
            (_window, _box) = (window!, box!);
            nint hwnd = _window.Dispatcher.Invoke(() => new System.Windows.Interop.WindowInteropHelper(_window).Handle);
            Desk.BringToFront(hwnd);   // a click on a window behind the foreground one can be refused
            Desk.ClickCentre(hwnd);   // a real click: focus, and a session break before any typing
            if (!Desk.WaitUntil(() => _window.Dispatcher.Invoke(() => _box.IsKeyboardFocused), 3000))
            {
                Dispose();
                Assert.Fail("the WPF box did not get focus");
            }
            Thread.Sleep(500);
        }

        public string Text => _box.Dispatcher.Invoke(() => _box.Text);

        public void Dispose() => _window.Dispatcher.Invoke(() => { _window.Close(); System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); });
    }

    [Fact]
    public void R11_3_a_WpfTextBox()
    {
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm);
        using var box = new WpfBox();
        Desk.Type("we saw teh cat ");
        Desk.WaitUntil(() => box.Text == "we saw the cat ");
        Assert.Equal(0, app.Desyncs);
        Assert.True(box.Text == "we saw the cat ", $"got \"{box.Text}\"; {app.Diagnostics()}");
    }

    [Fact]
    public void R4_8_b_TheUndoShortcutRevertsTheCorrection_R4_8_c()
    {
        // In a WPF box rather than Notepad: Notepad autocorrects misspellings itself, so there'd be nothing of ours to undo.
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm);
        using var box = new WpfBox();
        Desk.Type("we saw teh ");
        Assert.True(Desk.WaitUntil(() => box.Text == "we saw the "), $"got \"{box.Text}\"; {app.Diagnostics()}");
        Desk.Keys(VK_CONTROL, VK_MENU, (ushort)'Z');
        Assert.True(Desk.WaitUntil(() => box.Text == "we saw teh "), $"got \"{box.Text}\"; {app.Diagnostics()}");
        Assert.Equal(0, app.Desyncs);
        Assert.True(Desk.WaitUntil(() => File.Exists(Path.Combine(app.DataDir, "dictionary.txt")) && File.ReadAllText(Path.Combine(app.DataDir, "dictionary.txt")).Contains("teh")),
            $"4.8.c: the undone word was not added to the dictionary; {app.Diagnostics()}");
    }

    [Fact]
    public void R4_8_a_CtrlZIsLeftToTheApp_D34()
    {
        // D34: the user's apps keep their own Ctrl+Z. Ours never takes it, so nothing is learnt and no undo is counted.
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm);
        using var box = new WpfBox();
        Desk.Type("we saw teh ");
        Assert.True(Desk.WaitUntil(() => box.Text == "we saw the "), $"got \"{box.Text}\"; {app.Diagnostics()}");
        Desk.Keys(VK_CONTROL, (ushort)'Z');
        Thread.Sleep(1500);
        Assert.False(File.Exists(Path.Combine(app.DataDir, "dictionary.txt")) && File.ReadAllText(Path.Combine(app.DataDir, "dictionary.txt")).Contains("teh"),
            $"Ctrl+Z was taken by Global Autocorrect; {app.Diagnostics()}");
        Assert.Contains("\"undos\":0", File.ReadAllText(Path.Combine(app.DataDir, "counters.json")));
    }
}