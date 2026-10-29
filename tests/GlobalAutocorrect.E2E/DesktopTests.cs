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
    private static string Model(string s) => s
        .Replace("i went their yesterday", "I went there yesterday")
        .Replace("i went their", "I went there");

    /// <summary>
    /// Opens a fresh Notepad tab and, at the end, empties and closes it, so Notepad's session restore never keeps
    /// test text (Notepad autosaves tabs; 12.1 inputs avoid words Notepad corrects itself).
    /// </summary>
    private static (Window Window, TextBox Doc, Action Close) OpenNotepad(UIA3Automation uia)
    {
        var before = Process.GetProcessesByName("Notepad").Select(p => p.Id).ToHashSet();
        Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        Thread.Sleep(2500);
        var app = FlaUI.Core.Application.Attach("Notepad.exe");
        var win = app.GetMainWindow(uia, TimeSpan.FromSeconds(10))!;
        win.Focus();
        Desk.Keys(VK_CONTROL, (ushort)'N');   // a new, empty tab even if Notepad restored others
        Thread.Sleep(800);
        var doc = win.FindFirstDescendant(cf => cf.ByClassName("RichEditD2DPT"))!.AsTextBox();
        doc.Focus();
        void Close()
        {
            doc.Focus();
            Desk.Keys(VK_CONTROL, (ushort)'A');
            Desk.Keys(VK_DELETE);
            Desk.Keys(VK_CONTROL, (ushort)'W');   // empty tab closes without a prompt
            Thread.Sleep(500);
            foreach (var p in Process.GetProcessesByName("Notepad").Where(p => !before.Contains(p.Id))) p.CloseMainWindow();
        }
        return (win, doc, Close);
    }

    private static string Text(TextBox doc) => doc.Patterns.Text.Pattern.DocumentRange.GetText(-1).Replace("\r", "\n").TrimEnd('\n');

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
            Desk.Type("we saw teh bookk ");
            Desk.WaitUntil(() => Text(doc) == "we saw the book ");
            Assert.Equal(0, app.Desyncs);   // 11.3.d
            Assert.Equal("we saw the book ", Text(doc));
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
            Desk.Type("i went their yesterday. ");
            Desk.WaitUntil(() => Text(doc).StartsWith("I went there", StringComparison.Ordinal), 5000);
            Assert.Equal(0, app.Desyncs);
            Assert.Equal("I went there yesterday. ", Text(doc));
            Assert.True(llm.Requests >= 1);
        }
        finally { close(); }
    }

    [Fact]
    public void R4_8_a_CtrlZRevertsTheCorrection_R4_8_c()
    {
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm);
        using var uia = new UIA3Automation();
        var (_, doc, close) = OpenNotepad(uia);
        try
        {
            Desk.Type("we saw teh ");
            Assert.True(Desk.WaitUntil(() => Text(doc) == "we saw the "));
            Desk.Keys(VK_CONTROL, (ushort)'Z');
            Assert.True(Desk.WaitUntil(() => Text(doc) == "we saw teh "), Text(doc));
            Assert.Equal(0, app.Desyncs);
            Assert.Contains("teh", File.ReadAllText(Path.Combine(app.DataDir, "dictionary.txt")));
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
            Assert.True(Desk.WaitUntil(() => Text(doc) == "Please let me know"), Text(doc));
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
            Assert.True(Desk.WaitUntil(() => Text(doc) == "zq\t"), Text(doc));
        }
        finally { close(); }
    }

    [Fact]
    public void R11_3_a_WpfTextBox()
    {
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm);
        string text = "";
        using var ready = new ManualResetEventSlim();
        System.Windows.Window? window = null;
        System.Windows.Controls.TextBox? box = null;
        var ui = new Thread(() =>
        {
            box = new System.Windows.Controls.TextBox { FontSize = 16, AcceptsReturn = true };
            window = new System.Windows.Window { Title = "GA E2E WPF", Width = 600, Height = 200, Content = box };
            window.Loaded += (_, _) => { window.Activate(); box.Focus(); ready.Set(); };
            window.Show();
            System.Windows.Threading.Dispatcher.Run();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ready.Wait(10_000, TestContext.Current.CancellationToken);
        try
        {
            Thread.Sleep(800);
            Desk.Type("we saw teh cat ");
            Desk.WaitUntil(() => { box!.Dispatcher.Invoke(() => text = box.Text); return text == "we saw the cat "; });
            Assert.Equal(0, app.Desyncs);
            Assert.Equal("we saw the cat ", text);
        }
        finally
        {
            window?.Dispatcher.Invoke(() => { window.Close(); System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); });
        }
    }
}
