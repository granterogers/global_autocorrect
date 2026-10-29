using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using GlobalAutocorrect.Windows.Input;
using GlobalAutocorrect.Windows.Native;
using GlobalAutocorrect.Windows.Overlay;
using GlobalAutocorrect.Windows.Uia;
using GlobalAutocorrect.Windows.Write;
using Xunit;
using static GlobalAutocorrect.Windows.Native.NativeMethods;
using Path = System.IO.Path;

namespace GlobalAutocorrect.Windows.Tests;

/// <summary>Windows-layer checks that need no keyboard or screen. Live behaviour is covered by Spike B and E2E.</summary>
public sealed class WindowsLayerTests
{
    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "GlobalAutocorrect.slnx"))) d = d.Parent;
        return d!.FullName;
    }

    private static IEnumerable<string> Sources(string under) =>
        Directory.EnumerateFiles(Path.Combine(Repo(), under), "*.cs", SearchOption.AllDirectories)
                 .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    // ---- SendInput writer (10.3.c, 10.7) ------------------------------------------------------------

    [Fact]
    public void R10_7_a_OneBatchMovesLeftDeletesTypesAndMovesBack_R10_3_c()
    {
        var batch = SendInputWriter.Tagged.BuildEditBatch(caretDistance: 3, deleteCount: 2, replacement: "ab");
        ushort[] vks = batch.Where(i => (i.U.ki.dwFlags & KEYEVENTF_KEYUP) == 0).Select(i => i.U.ki.wVk).ToArray();
        Assert.Equal([VK_LEFT, VK_LEFT, VK_LEFT, VK_BACK, VK_BACK], vks[..5]);
        Assert.Equal([VK_RIGHT, VK_RIGHT, VK_RIGHT], vks[^3..]);
        Assert.Equal(2, vks.Length - 8);   // two typed characters in between
        Assert.Equal(batch.Length, batch.Count(i => i.type == INPUT_KEYBOARD));
    }

    [Fact]
    public void R4_8_a_HeldModifiersAreReleasedForTheBatchAndRestoredAfter_R4_8_b()
    {
        // The user still holds Ctrl+Alt from the global undo shortcut while our batch goes out.
        var writer = new SendInputWriter(InjectionTag.Value) { IsKeyDown = vk => vk is VK_LCONTROL or VK_LMENU };
        var batch = writer.BuildEditBatch(1, 1, "a");
        bool Up(INPUT i) => (i.U.ki.dwFlags & KEYEVENTF_KEYUP) != 0;
        Assert.Equal((VK_LCONTROL, true), (batch[0].U.ki.wVk, Up(batch[0])));
        Assert.Equal((VK_LMENU, true), (batch[1].U.ki.wVk, Up(batch[1])));
        Assert.Equal((VK_LMENU, false), (batch[^2].U.ki.wVk, Up(batch[^2])));
        Assert.Equal((VK_LCONTROL, false), (batch[^1].U.ki.wVk, Up(batch[^1])));
        // Nothing in between is sent while a modifier is down.
        Assert.DoesNotContain(batch[2..^2], i => i.U.ki.wVk is VK_LCONTROL or VK_LMENU or VK_RCONTROL or VK_RMENU);
        Assert.DoesNotContain(batch, i => i.U.ki.wVk is VK_LWIN or VK_RWIN);
    }

    [Fact]
    public void R10_7_b_EveryInjectedEventCarriesOurTag()
    {
        var tagged = SendInputWriter.Tagged.BuildEditBatch(5, 3, "Hello, World!");
        Assert.All(tagged, i => Assert.Equal(InjectionTag.Value, i.U.ki.dwExtraInfo));
        var untagged = new SendInputWriter(0).BuildTyping("abc");
        Assert.All(untagged, i => Assert.Equal((nuint)0, i.U.ki.dwExtraInfo));
    }

    [Fact]
    public void R10_7_a_ArrowKeysAreSentAsTheNavigationCluster()
    {
        var batch = SendInputWriter.Tagged.BuildEditBatch(1, 0, "");
        Assert.All(batch, i => Assert.True((i.U.ki.dwFlags & KEYEVENTF_EXTENDEDKEY) != 0));
    }

    [Fact]
    public void R10_7_c_NeverUsesValuePatternSetValue_R10_6_c()
    {
        // Any real use of UIA's value pattern needs its interface or its id (10002, UIA_ValuePatternId): neither may
        // appear, whether to write (10.7.c) or to guess the caret (10.6.c). Comments saying "never" are fine.
        foreach (var f in Sources("src"))
        {
            string text = File.ReadAllText(f);
            Assert.DoesNotContain("IUIAutomationValuePattern", text);
            Assert.DoesNotContain("UIA_ValuePatternId", text);
            Assert.DoesNotMatch(new Regex(@"GetCurrentPattern\(\s*10002\s*\)"), text);
        }
    }

    [Fact]
    public void R10_6_b_ReadsAWindowAroundTheCaretNotTheWholeDocument()
    {
        string reader = File.ReadAllText(Path.Combine(Repo(), "src", "GlobalAutocorrect.Windows", "Uia", "UiaReader.cs"));
        Assert.DoesNotContain("DocumentRange", reader);
        Assert.Contains("MoveEndpointByUnit", reader);
    }

    [Fact]
    public void R8_4_ExcelCellsAreRecognisedInAnyDisplayLanguage()
    {
        // A German Windows names the control type "Datenelement"; the id is the same everywhere.
        var cell = new UiaSnapshot(ReadStatus.Ok, "", "", "Selection", 1, "EXCEL", "Datenelement", "x", null, 50029);
        Assert.True(GlobalAutocorrect.Windows.Targets.AppExclusions.IsExcelCell(cell));
        var formulaBar = cell with { ControlType = "Bearbeiten", ControlTypeId = 50004 };   // Edit
        Assert.False(GlobalAutocorrect.Windows.Targets.AppExclusions.IsExcelCell(formulaBar));
    }

    // ---- No special cases (4.1.a, 2.10, 5.1.d) ---------------------------------------------------------

    [Fact]
    public void R4_1_a_NoAppIsSpecialCasedOutsideTheExclusionList()
    {
        // The correction path (Core and the Windows layer) must never branch on which app it is in; only the
        // exclusion list names apps. Comments are ignored.
        string[] apps = ["notepad", "winword", "outlook", "msedge", "chrome", "firefox", "slack", "teams"];
        var files = Sources(Path.Combine("src", "GlobalAutocorrect.Core")).Concat(Sources(Path.Combine("src", "GlobalAutocorrect.Windows")))
            .Where(f => !f.EndsWith("AppExclusions.cs", StringComparison.Ordinal));
        foreach (var f in files)
        {
            string code = string.Join('\n', File.ReadLines(f).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (Match m in Regex.Matches(code, "\"([A-Za-z]+)(\\.exe)?\""))
                Assert.DoesNotContain(m.Groups[1].Value.ToLowerInvariant(), apps);
        }
    }

    [Fact]
    public void R2_10_NoWordSpecificCorrectionTable()
    {
        // A table of typo → fix pairs would be a word-specific rule; corrections must come from the mechanisms.
        var pair = new Regex("\\[\"[a-z]+\"\\]\\s*=\\s*\"[a-z]+\"");
        foreach (var f in Sources(Path.Combine("src", "GlobalAutocorrect.Core")))
            Assert.DoesNotMatch(pair, File.ReadAllText(f));
    }

    [Fact]
    public void R5_1_d_LlamaSharpIsNotUsed()
    {
        foreach (var f in Directory.EnumerateFiles(Repo(), "*.csproj", SearchOption.AllDirectories).Append(Path.Combine(Repo(), "Directory.Packages.props")))
            Assert.DoesNotContain("LLamaSharp", File.ReadAllText(f), StringComparison.OrdinalIgnoreCase);
    }

    // ---- Key translation (4.2) -------------------------------------------------------------------------

    [Fact]
    public void R4_2_TranslatesKeysToTextWithoutCorruptingMemory()
    {
        // Regression: ToUnicodeEx once marshalled its buffer as ANSI and overran the native heap on every key.
        var tr = new KeyTranslator();
        var typed = new System.Text.StringBuilder();
        for (int i = 0; i < 5000; i++)
        {
            uint vk = (uint)('A' + i % 26);
            foreach (var k in tr.Translate(new RawInput(RawKind.Key, vk, MapVirtualKeyW(vk, MAPVK_VK_TO_VSC), 0, true, false, i, i)))
                if (k.Kind == GlobalAutocorrect.Core.Session.KeyKind.Char) typed.Append(k.Char);
            _ = tr.Translate(new RawInput(RawKind.Key, vk, 0, 0, false, false, i, i)).ToList();
        }
        Assert.StartsWith("abcdefghijklmnopqrstuvwxyz", typed.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5000, typed.Length);
        GC.Collect();   // a corrupted heap tends to fail here
        GC.WaitForPendingFinalizers();
    }

    [Fact]
    public void R4_2_PasteAndInjectedInputBreakTheSession_R14_4()
    {
        var tr = new KeyTranslator();
        Assert.Equal(GlobalAutocorrect.Core.Session.KeyKind.Foreign,
            tr.Translate(new RawInput(RawKind.Key, 'A', 0x1E, 0x10, true, true, 1, 1)).Single().Kind);
        _ = tr.Translate(new RawInput(RawKind.Key, VK_LCONTROL, 0x1D, 0, true, false, 2, 2)).ToList();
        Assert.Equal(GlobalAutocorrect.Core.Session.KeyKind.Paste,
            tr.Translate(new RawInput(RawKind.Key, 'V', 0x2F, 0, true, false, 3, 3)).Single().Kind);
    }

    // ---- UIA isolation (10.3.b) ------------------------------------------------------------------------

    [Fact]
    public void R10_3_b_AHungUiaCallIsAbandonedAndTheWorkerReplaced()
    {
        using var worker = new UiaWorker(timeoutMs: 300) { CooldownMs = 0 };
        var sw = Stopwatch.StartNew();
        int r = worker.Invoke(_ => { Thread.Sleep(10_000); return 1; }, onTimeout: -1);
        sw.Stop();
        Assert.Equal(-1, r);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, worker.Replacements);
        Assert.Equal(2, worker.Invoke(_ => 2, onTimeout: -1));   // the fresh thread works
    }

    [Fact]
    public void R10_3_b_AReadThatThrowsIsCantReadNotACrash()
    {
        // UIA timeouts arrive as TimeoutException, access denied as UnauthorizedAccessException: neither may escape.
        using var worker = new UiaWorker(timeoutMs: 1000);
        Assert.Equal(-1, worker.Invoke<int>(_ => throw new TimeoutException(), onTimeout: -1));
        Assert.Equal(-1, worker.Invoke<int>(_ => throw new UnauthorizedAccessException(), onTimeout: -1));
        Assert.Equal(5, worker.Invoke(_ => 5, onTimeout: -1));   // and the worker carries on
    }

    [Fact]
    public void R10_3_b_AfterAHangItBacksOffInsteadOfLeakingAThreadPerKey()
    {
        using var worker = new UiaWorker(timeoutMs: 200) { CooldownMs = 2000 };
        worker.Invoke(_ => { Thread.Sleep(5000); return 1; }, onTimeout: -1);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) Assert.Equal(-1, worker.Invoke(_ => 1, onTimeout: -1));
        Assert.True(sw.ElapsedMilliseconds < 500, "calls during the cool-down must return at once");
        Assert.Equal(1, worker.Replacements);
    }

    // ---- Hook and arms (10.3.a, 7.4, 4.8.a) ---------------------------------------------------------------

    [Fact]
    public void R10_3_a_HookInstallsAndRemovesWithoutBlocking()
    {
        var sw = Stopwatch.StartNew();
        using (var hook = new KeyboardHook()) hook.Start();
        Assert.True(sw.ElapsedMilliseconds < 3000);
    }

    [Fact]
    public void R4_8_a_ModifiersDoNotCountAsAKeyInBetween()
    {
        // Ctrl must not cancel the undo arm before the Z of Ctrl+Z arrives.
        foreach (ushort vk in new ushort[] { VK_CONTROL, VK_LCONTROL, VK_RCONTROL, VK_SHIFT, VK_LSHIFT, VK_MENU, VK_LWIN })
            Assert.True(KeyboardHook.IsModifier(vk));
        foreach (uint vk in new uint[] { 'Z', 'A', VK_TAB, VK_BACK, VK_LEFT })
            Assert.False(KeyboardHook.IsModifier(vk));
    }

    [Fact]
    public void R7_4_ArmIsOneShotAndOnlyForTheVeryNextKey_R4_8_a()
    {
        var arm = new InputArm();
        arm.ArmAt(41);
        Assert.False(arm.TryConsume(42));   // a key arrived in between: stale
        arm.ArmAt(41);
        Assert.True(arm.TryConsume(41));
        Assert.False(arm.TryConsume(41));   // one shot
        arm.ArmAt(7);
        arm.Disarm();
        Assert.False(arm.TryConsume(7));
    }

    // ---- Overlay (10.3.d, 7.6) ------------------------------------------------------------------------------

    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint h, int i);

    [Fact]
    public void R10_3_d_OverlayIsClickThroughNoActivateAndNotInAltTab_R7_6()
    {
        int ex = 0;
        bool visibleAfterShow = false, hiddenAfterHide = false;
        nint fgBefore = GetForegroundWindow(), fgAfter = 0;
        var t = new Thread(() =>
        {
            var o = new SuggestionOverlay(System.Windows.Media.Brushes.Gray);
            o.ShowAt("test", (-32000, -32000, 1, 16));   // off-screen: nothing flashes on the user's desktop
            ex = GetWindowLongW(new System.Windows.Interop.WindowInteropHelper(o).Handle, -20);
            fgAfter = GetForegroundWindow();
            visibleAfterShow = o.IsVisible;
            o.HideSuggestion();
            hiddenAfterHide = !o.IsVisible && !IsWindowVisible(new System.Windows.Interop.WindowInteropHelper(o).Handle);
            o.Close();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(10_000);
        Assert.True((ex & 0x20) != 0, "WS_EX_TRANSPARENT (click-through)");
        Assert.True((ex & 0x8000000) != 0, "WS_EX_NOACTIVATE");
        Assert.True((ex & 0x80) != 0, "WS_EX_TOOLWINDOW (not in Alt+Tab)");
        Assert.Equal(fgBefore, fgAfter);   // showing it never takes focus
        Assert.True(visibleAfterShow, "shown");
        Assert.True(hiddenAfterHide, "the suggestion really disappears (7.5)");
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
}
