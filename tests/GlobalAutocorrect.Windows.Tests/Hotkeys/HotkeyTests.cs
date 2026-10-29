using GlobalAutocorrect.Windows.Hotkeys;
using Xunit;

namespace GlobalAutocorrect.Windows.Tests.Hotkeys;

public sealed class HotkeyReentrancyTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [Fact]
    public void R10_3_e_RegisteringFromAPressedHandlerDoesNotDeadlock()
    {
        // The pause shortcut's handler re-registered shortcuts on the hotkey thread itself and hung it (found by the audit).
        using var m = new HotkeyManager();
        Assert.True(m.Register("outer", Hotkey.Parse("Ctrl+Alt+Shift+F22")));
        using var handled = new ManualResetEventSlim();
        m.Pressed += _ =>
        {
            m.Register("nested", Hotkey.Parse("Ctrl+Alt+Shift+F21"));   // runs on the hotkey thread
            handled.Set();
        };
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var hwnd = (IntPtr)typeof(HotkeyManager).GetField("_hwnd", flags)!.GetValue(m)!;
        int id = ((Dictionary<string, int>)typeof(HotkeyManager).GetField("_idsByName", flags)!.GetValue(m)!)["outer"];
        Assert.True(PostMessage(hwnd, 0x0312, id, IntPtr.Zero));   // a WM_HOTKEY as if the shortcut was pressed
        Assert.True(handled.Wait(5000, TestContext.Current.CancellationToken), "the handler's Register call hung the hotkey thread");
        Assert.True(m.Register("after", Hotkey.Parse("Ctrl+Alt+Shift+F20")), "the hotkey thread is no longer responsive");
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+P", "Ctrl+Alt+P")]
    [InlineData("ctrl + alt + z", "Ctrl+Alt+Z")]
    [InlineData("  CTRL+ALT+z ", "Ctrl+Alt+Z")]
    [InlineData("Ctrl+Shift+F12", "Ctrl+Shift+F12")]
    [InlineData("Shift+Ctrl+f23", "Ctrl+Shift+F23")]
    [InlineData("Alt+Space", "Alt+Space")]
    [InlineData("Win+Shift+Alt+Ctrl+9", "Ctrl+Alt+Shift+Win+9")]
    [InlineData("alt+pause", "Alt+Pause")]
    [InlineData("Ctrl+Insert", "Ctrl+Insert")]
    [InlineData("Ctrl+Alt+Home", "Ctrl+Alt+Home")]
    [InlineData("Ctrl+Alt+End", "Ctrl+Alt+End")]
    public void R10_3_e_ParsesAndFormatsShortcuts(string input, string canonical)
    {
        Assert.True(Hotkey.TryParse(input, out var hotkey));
        Assert.Equal(canonical, hotkey.ToString());

        // The canonical form round-trips to the same value.
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out var again));
        Assert.Equal(hotkey, again);
    }

    [Fact]
    public void R10_3_e_ParsedValuesMatchRegisterHotKeyConstants()
    {
        Assert.True(Hotkey.TryParse("Ctrl+Alt+Shift+Win+F1", out var h));
        Assert.Equal((HotkeyModifiers)15u, h.Modifiers);
        Assert.Equal(0x70u, h.Vk);

        Assert.True(Hotkey.TryParse("Ctrl+Alt+P", out var p));
        Assert.Equal(0x50u, p.Vk);
        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, p.Modifiers);
    }

    [Theory]
    [InlineData("Shift+A")]
    [InlineData("P")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Alt+P+Q")]
    [InlineData("Ctrl+Ctrl+P")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Enter")]
    public void R10_3_e_RejectsShortcutsWithoutCtrlAltOrWin(string input)
    {
        Assert.False(Hotkey.TryParse(input, out _));
    }

    [Fact]
    public void R8_2_DefaultPauseIsCtrlAltP()
    {
        Assert.Equal("Ctrl+Alt+P", Hotkey.DefaultPause.ToString());
    }

    [Fact]
    public void R4_8_b_DefaultUndoIsCtrlAltZ()
    {
        Assert.Equal("Ctrl+Alt+Z", Hotkey.DefaultUndo.ToString());
    }

    [Fact]
    public void R10_3_e_RegisterAndUnregisterAnUnusualShortcut()
    {
        var key = Hotkey.Parse("Ctrl+Alt+Shift+F23");
        using (var manager = new HotkeyManager())
        {
            Assert.True(manager.Register("test", key));
            manager.Unregister("test");

            // Re-registering the same name replaces the old registration.
            Assert.True(manager.Register("test", key));
            Assert.True(manager.Register("test", key));
            manager.Unregister("test");
        }

        // Disposed cleanly, so the combination is free again.
        using var second = new HotkeyManager();
        Assert.True(second.Register("again", key));
    }
}
