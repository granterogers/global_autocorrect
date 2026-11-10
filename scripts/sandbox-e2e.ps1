#requires -Version 7
<#
.SYNOPSIS
  D105 (2.8, 11.1.d): runs screen tests inside Windows Sandbox, so they never take over the user's screen, keyboard or
  running app, and can run at any time.

.DESCRIPTION
  Copies the Debug build of the app and the E2E tests, the browser test pages and the tuning set into a staging folder
  outside the repo, starts a sandbox with that folder (read-write, C:\ga) and this PC's .NET (read-only, C:\dotnet)
  shared, no network (the model stand-in is on the sandbox's own loopback), and the UK keyboard (1.9). The sandbox
  window is minimised straight away. The tests run as the sandbox's user, then the sandbox is closed and the results are
  copied to artifacts/<name>. The user's own app, files and keyboard are never touched.

  Build first (pwsh ./check.ps1 fast).

.EXAMPLE
  pwsh ./scripts/sandbox-e2e.ps1                                    # the real-typing test, every editor
  pwsh ./scripts/sandbox-e2e.ps1 -Filter GlobalAutocorrect.E2E.BrowserTests -Name browser
#>
param(
    [string]$Filter = 'GlobalAutocorrect.E2E.RealTypingTests',
    [string]$Name = 'realtyping',
    [int]$TimeoutMinutes = 60,
    [string]$Editors = '',   # D108: only these editors of the real-typing test, comma-separated
    [int]$Repeat = 1,

    [switch]$KeepOpen
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $env:LOCALAPPDATA 'GlobalAutocorrect\sandbox-stage'
$dotnet = Join-Path $env:ProgramFiles 'dotnet'
$appBin = Join-Path $repo 'src\GlobalAutocorrect.App\bin\Debug\net10.0-windows'
$e2eBin = Join-Path $repo 'tests\GlobalAutocorrect.E2E\bin\Debug\net10.0-windows'
foreach ($p in $appBin, $e2eBin) { if (-not (Test-Path $p)) { throw "Missing build output ${p}: run pwsh ./check.ps1 fast first." } }

# ---- staging (only copies; the repo and the user's install are never shared) ----------------------------------------
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\out", "$stage\repo\data\eval", "$stage\repo\tests" | Out-Null
Copy-Item $appBin "$stage\app" -Recurse
Copy-Item $e2eBin "$stage\e2e" -Recurse
Copy-Item (Join-Path $repo 'tests\pages') "$stage\repo\tests\pages" -Recurse
Copy-Item (Join-Path $repo 'data\eval\tuning.jsonl') "$stage\repo\data\eval\"
Copy-Item (Join-Path $repo 'GlobalAutocorrect.slnx') "$stage\repo\"
Copy-Item (Join-Path $repo 'scripts\sandbox-mute.ps1') "$stage\mute.ps1"
Copy-Item (Join-Path $repo 'scripts\sandbox-medium.ps1') "$stage\medium.ps1"

@"
@echo off
rem D105: runs inside Windows Sandbox as its signed-in user.
set DOTNET_ROOT=C:\dotnet
set PATH=C:\dotnet;%PATH%
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set GA_E2E=1
set CI=true
set GA_REPO=C:\ga\repo
set GA_E2E_APP=C:\ga\app
set GA_PROBE=1
set GA_RT_EDITORS=$Editors
set GA_RT_REPEAT=$Repeat
powershell -NoProfile -Command "Set-WinUserLanguageList -LanguageList en-GB -Force; Set-WinDefaultInputMethodOverride -InputTip '0809:00000809'" > C:\ga\out\setup.log 2>&1
C:\ga\e2e\GlobalAutocorrect.E2E.exe -class $($Filter -replace ',', ' -class ') > C:\ga\out\run.log 2>&1
echo %ERRORLEVEL% > C:\ga\out\done.txt
"@ | Set-Content "$stage\run.cmd" -Encoding ascii

$config = @"
<Configuration>
  <Networking>Disable</Networking>
  <vGPU>Enable</vGPU>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <AudioInput>Disable</AudioInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <VideoInput>Disable</VideoInput>
  <MemoryInMB>8192</MemoryInMB>
  <MappedFolders>
    <MappedFolder><HostFolder>$stage</HostFolder><SandboxFolder>C:\ga</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$dotnet</HostFolder><SandboxFolder>C:\dotnet</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
  </MappedFolders>
</Configuration>
"@

if ((wsb list --raw | ConvertFrom-Json).WindowsSandboxEnvironments.Count -gt 0) { throw "A Windows Sandbox is already running; close it first (only one can run)." }
$started = wsb start --config $config --raw | ConvertFrom-Json
$id = $started.Id
"sandbox $id started"

# Keep the user's desktop theirs: minimise the sandbox window as soon as it appears.
# Found by its title: the window belongs to a helper process, not one named after the sandbox.
Add-Type -Namespace SB -Name Win -MemberDefinition @"
delegate bool EnumProc(System.IntPtr h, System.IntPtr p);
[DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, System.IntPtr p);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] static extern bool IsWindowVisible(System.IntPtr h);
[DllImport("user32.dll")] static extern bool IsIconic(System.IntPtr h);
[DllImport("user32.dll")] static extern bool ShowWindowAsync(System.IntPtr h, int cmd);
public static int MinimiseTitled(string title) {
    int n = 0;
    EnumWindows((h, p) => {
        var s = new System.Text.StringBuilder(256); GetWindowText(h, s, 256);
        if (s.ToString() == title && IsWindowVisible(h)) { ShowWindowAsync(h, 0); n++; }
        return true; }, System.IntPtr.Zero);
    return n;
}
"@
function Hide-Sandbox { [SB.Win]::MinimiseTitled('Windows Sandbox') | Out-Null }   # SW_HIDE, as the tray icon does (backup)

$code = $null
try {
    # The sandbox's user signs in only once a window is connected to it (wsb start alone has no session).
    # D105, user request "run it in the background with the icon in the system tray": a tray icon that keeps the window
    # hidden (Show / Hide / Stop in its menu), started before the window can appear; it ends when the sandbox does.
    Start-Process "$env:SystemRoot\System32\conhost.exe" -ArgumentList '--headless', "`"$((Get-Command pwsh).Source)`"", '-NoProfile', '-STA', '-File', "`"$PSScriptRoot\sandbox-tray.ps1`"", '-Label', "`"$Name`""
    Start-Process wsb -ArgumentList 'connect', '--id', $id -WindowStyle Hidden
    $deadline = (Get-Date).AddMinutes(5)
    while ($true) {
        Hide-Sandbox
        $r = wsb exec --id $id -c 'cmd /c echo ready' -r ExistingLogin 2>&1
        if ($LASTEXITCODE -eq 0) { break }
        if ((Get-Date) -gt $deadline) { throw "the sandbox's user never signed in: $r" }
        Start-Sleep 1
    }
    # Never a sound on the user's PC: mute the sandbox before anything runs (it starts with sound on).
    wsb exec --id $id -c 'cmd /c powershell -NoProfile -ExecutionPolicy Bypass -File C:\ga\mute.ps1 > C:\ga\out\mute.log 2>&1' -r ExistingLogin | Out-Null
    "sandbox ready ($((Get-Content "$stage\out\mute.log" -ErrorAction SilentlyContinue) -join ' ')); running $Filter"
    # Everything in the sandbox runs as administrator, which the app refuses (10.8), and an elevated run would not stand
    # in for the user anyway: run the tests, and so the app, Edge and Notepad they start, at medium integrity (D105).
    $run = 'cmd /c powershell -NoProfile -ExecutionPolicy Bypass -File C:\ga\medium.ps1 "cmd /c C:\ga\run.cmd" > C:\ga\out\medium.log 2>&1'
    wsb exec --id $id -c $run -r ExistingLogin | Out-Null
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while (-not (Test-Path "$stage\out\done.txt")) {
        Hide-Sandbox
        if ((Get-Date) -gt $deadline) { throw "the tests did not finish in $TimeoutMinutes minutes" }
        Start-Sleep 10
    }
    $code = [int]((Get-Content "$stage\out\done.txt" -Raw).Trim())
}
finally {
    if (-not $KeepOpen) { wsb stop --id $id 2>&1 | Out-Null }
    $dest = Join-Path $repo "artifacts\$Name"
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item "$stage\out\*" $dest -Recurse -ErrorAction SilentlyContinue
    if (Test-Path "$stage\repo\artifacts") { Copy-Item "$stage\repo\artifacts\*" $dest -Recurse -Force -ErrorAction SilentlyContinue }
}
Get-ChildItem (Join-Path $repo "artifacts\$Name") -Recurse -Filter summary.md | ForEach-Object { Get-Content $_.FullName }
"exit code: $code"
exit ($code ?? 1)
