#requires -Version 7
<#
.SYNOPSIS
  D81: publishes what this PC's Global Autocorrect learned per program (counts only, never text) into the GitHub source.

.DESCRIPTION
  Reads %APPDATA%\GlobalAutocorrect\apphealth.json (per program: fixes written, failed checks, stuck fields, undos),
  writes it as this PC's file in data/learning (replaced, never added to), rebuilds assets/app-profiles.json from every
  PC's file, runs the fast checks, and commits and pushes only those files. Programs with fewer than 5 events are left
  out. Nothing is committed if nothing changed, if the checks fail, or if the branch isn't main.

  -Register adds a daily Windows scheduled task that runs this script (default 18:00); -Unregister removes it.
#>
param(
    [switch]$Register,
    [switch]$Unregister,
    [string]$At = '18:00',
    [switch]$NoPush
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$task = 'Global Autocorrect - publish learning'

if ($Unregister) {
    Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    "Removed the daily task '$task'."
    return
}
if ($Register) {
    $action = New-ScheduledTaskAction -Execute (Get-Command pwsh).Source -Argument "-NoProfile -WindowStyle Hidden -File `"$PSCommandPath`"" -WorkingDirectory $repo
    $trigger = New-ScheduledTaskTrigger -Daily -At $At
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopOnIdleEnd -ExecutionTimeLimit (New-TimeSpan -Minutes 20)
    Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Settings $settings -Description 'D81: publish what Global Autocorrect learned per program (counts only) to the GitHub source.' -Force | Out-Null
    "Registered the daily task '$task' at $At."
    return
}

$data = Join-Path $env:APPDATA 'GlobalAutocorrect'
$local = Join-Path $data 'apphealth.json'
if (-not (Test-Path $local) -and -not (Test-Path (Join-Path $data 'mistakes.json'))) { "learning: nothing learned yet on this PC"; return }

# A random id for this PC's file, so each PC replaces only its own counts. It says nothing about the PC or the user.
$idFile = Join-Path $data 'install-id'
if (-not (Test-Path $idFile)) { [guid]::NewGuid().ToString('N').Substring(0, 12) | Set-Content $idFile -NoNewline }
$pc = (Get-Content $idFile -Raw).Trim()

$log = Join-Path $data 'publish-learning.log'
function Log($m) { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $m" | Add-Content $log; $m }

Push-Location $repo
try {
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'main') { Log "learning: on branch $branch, not main: nothing published"; return }

    $paths = @('data/learning', 'assets/app-profiles.json', 'assets/mistakes.json')   # D81, D84
    dotnet run --project tools/GlobalAutocorrect.DataGen -c Release -- learning --local $local --pc $pc --dir data/learning --out assets/app-profiles.json
    if ($LASTEXITCODE -ne 0) { throw "DataGen learning failed" }

    git add -- $paths
    git diff --cached --quiet -- $paths
    if ($LASTEXITCODE -eq 0) { Log "learning: nothing new to publish"; return }

    pwsh -NoProfile -File ./check.ps1 fast
    if ($LASTEXITCODE -ne 0) { git restore --staged -- $paths; Log "learning: fast checks failed: nothing published"; exit 1 }

    # Only these files go in the commit, whatever else is being worked on in the checkout.
    git commit -q -m "Learned program profiles from real use (D81, counts only)" -- $paths
    if ($LASTEXITCODE -ne 0) { throw "commit failed" }
    if (-not $NoPush) {
        git push
        if ($LASTEXITCODE -ne 0) { Log "learning: committed but the push failed; it goes with the next push"; exit 1 }
    }
    Log "learning: published $(git rev-parse --short HEAD)"
}
finally { Pop-Location }
