#requires -Version 7
<#
.SYNOPSIS
  D105: while a test sandbox runs, keeps its window hidden and shows a tray icon instead (user request: "run it in the
  background with the icon in the system tray"). Started by sandbox-e2e.ps1; ends by itself when no sandbox is running.

.DESCRIPTION
  The sandbox's window (title "Windows Sandbox") is hidden whenever it appears, checked 10 times a second, without
  taking focus. The tray icon's menu: Show the sandbox window, Hide it again, or Stop the test run. Its tooltip says what
  is running.
#>
param([string]$Label = 'tests')
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -Namespace SBT -Name Win -MemberDefinition @"
delegate bool EnumProc(System.IntPtr h, System.IntPtr p);
[DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, System.IntPtr p);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] static extern bool IsWindowVisible(System.IntPtr h);
[DllImport("user32.dll")] static extern bool ShowWindowAsync(System.IntPtr h, int cmd);
[DllImport("user32.dll")] static extern bool SetForegroundWindow(System.IntPtr h);
public static void Set(string title, bool show) {
    EnumWindows((h, p) => {
        var s = new System.Text.StringBuilder(256); GetWindowText(h, s, 256);
        if (s.ToString() == title) {
            if (!show && IsWindowVisible(h)) ShowWindowAsync(h, 0);                       // SW_HIDE
            else if (show && !IsWindowVisible(h)) { ShowWindowAsync(h, 9); SetForegroundWindow(h); }   // SW_RESTORE
        }
        return true; }, System.IntPtr.Zero);
}
"@

$show = $false
$icon = [System.Windows.Forms.NotifyIcon]::new()
$icon.Icon = [System.Drawing.SystemIcons]::Application
$icon.Text = "Global Autocorrect: $Label running in Windows Sandbox"
$menu = [System.Windows.Forms.ContextMenuStrip]::new()
$menu.Items.Add('Show the sandbox window', $null, { $script:show = $true; [SBT.Win]::Set('Windows Sandbox', $true) }) | Out-Null
$menu.Items.Add('Hide the sandbox window', $null, { $script:show = $false; [SBT.Win]::Set('Windows Sandbox', $false) }) | Out-Null
$menu.Items.Add('Stop the test run', $null, {
    foreach ($e in (wsb list --raw | ConvertFrom-Json).WindowsSandboxEnvironments) { wsb stop --id $e.Id | Out-Null }
}) | Out-Null
$icon.ContextMenuStrip = $menu
$icon.Visible = $true

$timer = [System.Windows.Forms.Timer]::new()
$timer.Interval = 100
$script:ticks = 0
$timer.Add_Tick({
    if (-not $script:show) { [SBT.Win]::Set('Windows Sandbox', $false) }
    # Every 5 seconds: is any sandbox still running? If not, the icon goes.
    if ((++$script:ticks % 50) -eq 0) {
        $running = @((wsb list --raw 2>$null | ConvertFrom-Json).WindowsSandboxEnvironments).Count
        if ($running -eq 0 -and $script:ticks -gt 300) { $timer.Stop(); $icon.Visible = $false; [System.Windows.Forms.Application]::Exit() }
    }
})
$timer.Start()
[System.Windows.Forms.Application]::Run()
$icon.Dispose()
