#requires -Version 7
<#
.SYNOPSIS
  D182 (13.1, 13.2): installs Global Autocorrect on a clean Windows, in Windows Sandbox, the way someone new does, and
  checks the app really runs. Nothing of this PC is lent to the sandbox: no .NET, no PowerShell 7, no model cache.

.DESCRIPTION
  Found by doing it: the release needed .NET's Desktop Runtime, which this PC and GitHub's test machines have and a new
  user's PC doesn't, so the app never started, yet the installer said it was running. The screen tests (sandbox-e2e.ps1)
  couldn't see that: they lend the sandbox this PC's .NET.

  Without -Package: the README's one-line command, exactly, so the latest release and get.ps1 on main.
  With -Package: get.ps1 from this checkout, installing the newest artifacts\GlobalAutocorrect-*.zip (build it with
  scripts\package.ps1) in place of the release: the test before releasing.

  The sandbox has no winget, so this also tests get.ps1 installing PowerShell 7 itself. It downloads the model (about
  5.5 GB). Runs at medium integrity, as a user's own PowerShell window does (D105), with the window hidden behind a tray
  icon. Admin requests (the Visual C++ runtime's install) are allowed without asking, standing in for the user's Yes.
  Results go to artifacts\sandbox-install; exit code 0 only if the app is running .NET, llama.cpp's server runs, and
  the app has its Start menu entry and starts with Windows.

.EXAMPLE
  pwsh ./scripts/sandbox-install-test.ps1              # the released one-liner
  pwsh ./scripts/sandbox-install-test.ps1 -Package     # this checkout's get.ps1 and package
#>
param(
    [switch]$Package,
    [int]$TimeoutMinutes = 40,
    [switch]$KeepOpen
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $env:LOCALAPPDATA 'GlobalAutocorrect\sandbox-install'
$oneLiner = 'irm https://raw.githubusercontent.com/granterogers/global_autocorrect/main/get.ps1 | iex'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\out" | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'sandbox-mute.ps1') "$stage\mute.ps1"
Copy-Item (Join-Path $PSScriptRoot 'sandbox-medium.ps1') "$stage\medium.ps1"

if ($Package) {
    $zip = Get-ChildItem (Join-Path $repo 'artifacts') -Filter 'GlobalAutocorrect-*.zip' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $zip) { throw 'No artifacts\GlobalAutocorrect-*.zip: run pwsh ./scripts/package.ps1 first.' }
    New-Item -ItemType Directory -Force "$stage\pkg" | Out-Null
    Copy-Item $zip.FullName "$stage\pkg"
    "$((Get-FileHash $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($zip.Name)" | Set-Content "$stage\pkg\SHA256SUMS.txt" -Encoding ascii
    Copy-Item (Join-Path $repo 'get.ps1') "$stage\get.ps1"
    $install = 'powershell -NoProfile -ExecutionPolicy Bypass -c "Get-Content C:\ga\get.ps1 -Raw | iex"'
    $zipEnv = "C:\ga\pkg\$($zip.Name)"
    "testing $($zip.Name) with this checkout's get.ps1"
}
else {
    $install = "powershell -ExecutionPolicy Bypass -c `"$oneLiner`""
    $zipEnv = ''
    'testing the README''s one-line command (latest release)'
}

# What the clean machine has before anything is installed.
@'
$os = Get-CimInstance Win32_OperatingSystem
"windows: $($os.Caption) $($os.Version)"
"winget: $([bool](Get-Command winget -ErrorAction SilentlyContinue))"
"pwsh: $([bool](Get-Command pwsh -ErrorAction SilentlyContinue))"
"dotnet desktop runtime: $(Test-Path "$env:ProgramFiles\dotnet\shared\Microsoft.WindowsDesktop.App")"
"gpu: $((Get-CimInstance Win32_VideoController | ForEach-Object Name) -join ', ')"
'@ | Set-Content "$stage\facts.ps1" -Encoding utf8

# Did it work? The app's process alone isn't proof (Windows' ".NET is missing" box is that process): .NET must be
# running in it. Counts and states only, never anything typed (8.6).
@'
$exe = Join-Path $env:LOCALAPPDATA 'Programs\GlobalAutocorrect\GlobalAutocorrect.exe'
Start-Sleep -Seconds 20
$p = Get-Process GlobalAutocorrect -ErrorAction SilentlyContinue | Where-Object Path -eq $exe | Select-Object -First 1
$dotnet = $false
if ($p) { try { $dotnet = [bool]($p.Modules | Where-Object ModuleName -eq 'coreclr.dll') } catch { } }
$startMenu = Test-Path (Join-Path ([Environment]::GetFolderPath('Programs')) 'Global Autocorrect.lnk')
$run = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).GlobalAutocorrect
$own = Join-Path $env:LOCALAPPDATA 'GlobalAutocorrect\pwsh\pwsh.exe'
"installed: $(Test-Path $exe)"
"build: $(Get-Content (Join-Path (Split-Path $exe) 'build.txt') -ErrorAction SilentlyContinue)"
"process running: $([bool]$p)"
".NET running in it: $dotnet"
"start menu entry: $startMenu"
"starts with Windows: $([bool]$run)"
# The AI itself: llama.cpp's server must load (a missing runtime DLL shows Windows' error box and never exits).
$srv = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'GlobalAutocorrect\cache\llama') -Recurse -Filter llama-server.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -match '\\cuda$' } | Select-Object -First 1
$ai = $false
if ($srv) {
    $sp = Start-Process $srv.FullName -ArgumentList '--version' -PassThru -WindowStyle Hidden -RedirectStandardOutput C:\ga\out\ai-version.txt -RedirectStandardError C:\ga\out\ai-version-err.txt
    $null = $sp.Handle   # Windows PowerShell only reports the exit code of a process whose handle was read while it ran
    if ($sp.WaitForExit(30000)) { $ai = $sp.ExitCode -eq 0 -and ((Get-Content C:\ga\out\ai-version-err.txt, C:\ga\out\ai-version.txt -Raw) -join '') -match 'version: ' } else { $sp.Kill() }
}
"AI server runs (llama-server --version): $ai"
"PowerShell 7: $(if (Test-Path $own) { "unpacked by get.ps1 ($own)" } elseif (Test-Path "$env:ProgramFiles\PowerShell\7\pwsh.exe") { 'Program Files' } else { 'none' })"
$crash = Join-Path $env:APPDATA 'GlobalAutocorrect\crash.txt'
if (Test-Path $crash) { 'crash.txt:'; Get-Content $crash -TotalCount 5 }
"RESULT: $(if ($dotnet -and $ai -and $startMenu -and $run) { 'PASS' } else { 'FAIL' })"
try {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save('C:\ga\out\screen.png'); $g.Dispose(); $bmp.Dispose()
} catch { "screenshot failed: $_" }
'@ | Set-Content "$stage\check.ps1" -Encoding utf8

@"
@echo off
rem D182: runs inside Windows Sandbox at medium integrity, as a user's own PowerShell window does.
set GA_GET_ZIP=$zipEnv
powershell -NoProfile -ExecutionPolicy Bypass -File C:\ga\facts.ps1 > C:\ga\out\facts.txt 2>&1
$install > C:\ga\out\install.txt 2>&1
echo %ERRORLEVEL% > C:\ga\out\install-exit.txt
powershell -NoProfile -ExecutionPolicy Bypass -File C:\ga\check.ps1 > C:\ga\out\result.txt 2>&1
echo done > C:\ga\out\done.txt
"@ | Set-Content "$stage\run.cmd" -Encoding ascii

# Networking on (it downloads, as a user's PC does); the GPU shared, as on a real PC; nothing else of this PC.
$config = @"
<Configuration>
  <vGPU>Enable</vGPU>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <AudioInput>Disable</AudioInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <VideoInput>Disable</VideoInput>
  <MemoryInMB>8192</MemoryInMB>
  <MappedFolders>
    <MappedFolder><HostFolder>$stage</HostFolder><SandboxFolder>C:\ga</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
</Configuration>
"@

if ((wsb list --raw | ConvertFrom-Json).WindowsSandboxEnvironments.Count -gt 0) { throw 'A Windows Sandbox is already running; close it first (only one can run).' }
$id = (wsb start --config $config --raw | ConvertFrom-Json).Id
"sandbox $id started"
$started = Get-Date
try {
    # D105: the window stays hidden behind a tray icon (Show / Hide / Stop); the user's desktop stays theirs.
    Start-Process "$env:SystemRoot\System32\conhost.exe" -ArgumentList '--headless', "`"$((Get-Command pwsh).Source)`"", '-NoProfile', '-STA', '-File', "`"$PSScriptRoot\sandbox-tray.ps1`"", '-Label', '"install test"'
    Start-Process wsb -ArgumentList 'connect', '--id', $id -WindowStyle Hidden
    $deadline = (Get-Date).AddMinutes(5)
    while ($true) {
        $r = wsb exec --id $id -c 'cmd /c echo ready' -r ExistingLogin 2>&1
        if ($LASTEXITCODE -eq 0) { break }
        if ((Get-Date) -gt $deadline) { throw "the sandbox's user never signed in: $r" }
        Start-Sleep 1
    }
    wsb exec --id $id -c 'cmd /c powershell -NoProfile -ExecutionPolicy Bypass -File C:\ga\mute.ps1 > C:\ga\out\mute.log 2>&1' -r ExistingLogin | Out-Null
    # Installing the Visual C++ runtime asks Windows for admin rights (D182). Nobody can click Yes in a hidden sandbox, so
    # the sandbox allows admin requests without asking: it stands in for the user allowing it.
    wsb exec --id $id -c 'cmd /c reg add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System /v ConsentPromptBehaviorAdmin /t REG_DWORD /d 0 /f > C:\ga\out\uac.log 2>&1' -r System | Out-Null
    wsb exec --id $id -c 'cmd /c powershell -NoProfile -ExecutionPolicy Bypass -File C:\ga\medium.ps1 "cmd /c C:\ga\run.cmd" > C:\ga\out\medium.log 2>&1' -r ExistingLogin | Out-Null
    'installing (the model download takes a few minutes)'
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while (-not (Test-Path "$stage\out\done.txt")) {
        if ((Get-Date) -gt $deadline) { throw "the install did not finish in $TimeoutMinutes minutes" }
        Start-Sleep 10
    }
}
finally {
    if (-not $KeepOpen) { wsb stop --id $id 2>&1 | Out-Null }
    $dest = Join-Path $repo 'artifacts\sandbox-install'
    # The folder's contents, not the folder: a window or shell open in it would otherwise stop the results being copied.
    New-Item -ItemType Directory -Force $dest | Out-Null
    Get-ChildItem $dest -Force | Remove-Item -Recurse -Force
    Copy-Item "$stage\out\*" $dest -Recurse -ErrorAction SilentlyContinue
}
"took $([math]::Round(((Get-Date) - $started).TotalMinutes, 1)) min; results in artifacts\sandbox-install"
'--- clean machine:'; Get-Content "$dest\facts.txt"
'--- installer (last lines):'; Get-Content "$dest\install.txt" | Where-Object { $_.Trim() -and $_ -notmatch '^\s*\d+\s+[\d.]+[GM]' } | Select-Object -Last 12
"installer exit code: $((Get-Content "$dest\install-exit.txt" -ErrorAction SilentlyContinue) -join '')"
'--- result:'; Get-Content "$dest\result.txt"
exit $(if ((Get-Content "$dest\result.txt" -Raw) -match 'RESULT: PASS') { 0 } else { 1 })
