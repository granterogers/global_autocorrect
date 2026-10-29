using GlobalAutocorrect.Core.Targets;
using GlobalAutocorrect.Windows.Targets;
using GlobalAutocorrect.Windows.Uia;
using Xunit;

namespace GlobalAutocorrect.Windows.Tests;

public class TargetGateTests
{
    private static UiaSnapshot Snap(string process, string controlType = "edit", int pid = 1234) =>
        new(ReadStatus.Ok, "", "", "TextPattern2", pid, process, controlType, "", null);

    [Fact]
    public void R8_4_DefaultsExcludeTerminalsEditorsPasswordManagersRemoteDesktop()
    {
        var ex = new AppExclusions(AppExclusions.Defaults);
        foreach (var name in new[] { "WindowsTerminal", "cmd", "pwsh", "Code", "devenv", "KeePassXC", "1Password", "mstsc", "AnyDesk", "steam" })
        {
            Assert.True(ex.IsExcludedProcess(name), name);
        }
        Assert.True(ex.IsExcludedProcess("windowsterminal"));
        Assert.True(ex.IsExcludedProcess("BITWARDEN"));
        Assert.False(ex.IsExcludedProcess("WINWORD"));
        Assert.False(ex.IsExcludedProcess("notepad"));
    }

    [Fact]
    public void R8_4_ListIsEditable()
    {
        var ex = new AppExclusions(["Foo"]);
        Assert.True(ex.IsExcludedProcess("Foo"));
        Assert.True(ex.IsExcludedProcess("foo"));
        Assert.False(ex.IsExcludedProcess("WindowsTerminal"));
    }

    [Fact]
    public void R8_4_ExcelCellsExcluded()
    {
        Assert.True(AppExclusions.IsExcelCell(Snap("EXCEL", "data item")));
        Assert.True(AppExclusions.IsExcelCell(Snap("excel", "Cell")));
        Assert.False(AppExclusions.IsExcelCell(Snap("WINWORD", "document")));
        Assert.False(AppExclusions.IsExcelCell(Snap("EXCEL", "edit")));
    }

    [Fact]
    public void R10_8_OwnProcessIsNotElevatedAboveItself()
    {
        Assert.False(IntegrityLevel.IsElevatedAboveUs(Environment.ProcessId));
        Assert.True(IntegrityLevel.Current >= 0x2000);
    }

    [Fact]
    public void R10_8_InaccessibleProcessCountsAsElevated()
    {
        // pid 4 is the System process: not openable from a normal process.
        Assert.True(IntegrityLevel.IsElevatedAboveUs(4));
    }

    [Fact]
    public void R10_8_GateReportsElevated()
    {
        var gate = TargetGate.Create(new AppExclusions([]), isElevated: pid => pid == 99, foregroundIsFullScreen: () => false);
        Assert.Equal(TargetStatus.Elevated, gate(Snap("notepad", pid: 99)));
        Assert.Null(gate(Snap("notepad", pid: 100)));
    }

    [Fact]
    public void R8_3_GateAllowsNormalApps()
    {
        var gate = TargetGate.Create(new AppExclusions(AppExclusions.Defaults), isElevated: _ => false, foregroundIsFullScreen: () => false);
        Assert.Null(gate(Snap("WINWORD", "document")));
        Assert.Null(gate(Snap("notepad")));
        Assert.Equal(TargetStatus.Excluded, gate(Snap("WindowsTerminal")));
        Assert.Equal(TargetStatus.Excluded, gate(Snap("EXCEL", "data item")));

        var fullScreen = TargetGate.Create(new AppExclusions([]), isElevated: _ => false, foregroundIsFullScreen: () => true);
        Assert.Equal(TargetStatus.Excluded, fullScreen(Snap("notepad")));
    }
}
