#Requires -Version 7
<#
.SYNOPSIS
  Build, test and install the current code on this PC in one go: fast tests, package, then install.ps1 (which closes
  the running copy cleanly, replaces it and starts the new one). Claude Code runs this after every change the user
  asks for, so the installed app is always the latest build.
.EXAMPLE
  pwsh ./scripts/deploy.ps1
#>
param([switch] $SkipTests)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $SkipTests) {
    & pwsh -NoProfile -File (Join-Path $repo 'check.ps1') fast
    if ($LASTEXITCODE -ne 0) { throw 'fast tests failed: nothing was installed' }
}
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'package.ps1')
if ($LASTEXITCODE -ne 0) { throw 'packaging failed: nothing was installed' }

# Installing restarts the app, which is away for a few seconds: wait until the user has stopped typing (no key or mouse
# input for 15 s) so a reply they're in the middle of isn't left uncorrected. After 5 minutes it goes ahead anyway.
Add-Type -Namespace GaDeploy -Name Input -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
public static uint IdleMs() { var i = new LASTINPUTINFO(); i.cbSize = 8; GetLastInputInfo(ref i); return unchecked((uint)System.Environment.TickCount - i.dwTime); }
'@
$waited = [Diagnostics.Stopwatch]::StartNew()
$said = $false
while ([GaDeploy.Input]::IdleMs() -lt 15000 -and $waited.Elapsed.TotalMinutes -lt 5) {
    if (-not $said) { Write-Host '  waiting for a 15-second break in typing before restarting the app'; $said = $true }
    Start-Sleep -Milliseconds 500
}

& pwsh -NoProfile -File (Join-Path $repo 'dist/GlobalAutocorrect/install.ps1')
if ($LASTEXITCODE -ne 0) { throw 'install failed' }
