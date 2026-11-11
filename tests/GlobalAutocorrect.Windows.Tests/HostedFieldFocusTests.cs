using GlobalAutocorrect.Windows.Targets;
using Xunit;

namespace GlobalAutocorrect.Windows.Tests;

/// <summary>D123, user report (new Teams: "why not realise picked up?"): every fix there was dropped as "not-sent".</summary>
public sealed class HostedFieldFocusTests
{
    [Fact]
    public void R12_2_D123_AFieldInAHostedBrowserEngineCountsAsFocusedInItsAppsWindow()
    {
        const int teamsWindow = 100, webview = 200, other = 300;
        Assert.True(UiaTextTarget.SameFocus(teamsWindow, fieldPid: webview, windowPidAtRead: teamsWindow));   // Teams: field in msedgewebview2
        Assert.True(UiaTextTarget.SameFocus(webview, fieldPid: webview, windowPidAtRead: webview));            // an ordinary app
        Assert.False(UiaTextTarget.SameFocus(other, fieldPid: webview, windowPidAtRead: teamsWindow));          // the user switched app: nothing typed
        Assert.False(UiaTextTarget.SameFocus(0, fieldPid: 0, windowPidAtRead: 0));
    }
}
