#requires -Version 7
<#
.SYNOPSIS
  D81: publishes what this PC's Global Autocorrect learned per program (counts only, never text) into the GitHub source.

.DESCRIPTION
  Reads %APPDATA%\GlobalAutocorrect\apphealth.json (per program: fixes written, failed checks, stuck fields, undos),
  writes it as this PC's file in data/learning (replaced, never added to), rebuilds the brain's profiles and mistakes
  (brain/brain.json, D102) from every PC's file, and commits and pushes only those files. Programs with fewer than 5
  events are left out. Nothing is committed if nothing changed, if the brain written wouldn't load, or if the branch isn't
  main.

  D102: the brain is data, so no build, tests or release follow: every running copy downloads it within 15 minutes.

  -Register adds a daily Windows scheduled task that runs this script (default 18:00) with no window at all (conhost
  --headless: Windows Terminal ignores -WindowStyle Hidden and opened a window); -Unregister removes it.
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
    # D102: conhost --headless gives the script (and the dotnet and git it runs) a console nobody sees.
    $conhost = Join-Path $env:SystemRoot 'System32\conhost.exe'
    $action = New-ScheduledTaskAction -Execute $conhost -Argument "--headless `"$((Get-Command pwsh).Source)`" -NoProfile -NonInteractive -File `"$PSCommandPath`"" -WorkingDirectory $repo
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

    $paths = @('data/learning', 'brain/brain.json')   # D81, D84, D102
    $out = dotnet run --project tools/GlobalAutocorrect.DataGen -c Release -- learning --local $local --pc $pc --dir data/learning --out brain/brain.json 2>&1
    if ($LASTEXITCODE -ne 0) { Log "learning: DataGen failed: nothing published"; exit 1 }
    Log ($out | Select-Object -Last 1)

    git add -- $paths
    git diff --cached --quiet -- $paths
    if ($LASTEXITCODE -eq 0) { Log "learning: nothing new to publish"; return }


    # Only these files go in the commit, whatever else is being worked on in the checkout.
    git commit -q -m "Brain: learned from real use (D81, D84, D102, counts only)" -- $paths
    if ($LASTEXITCODE -ne 0) { throw "commit failed" }
    # Only the brain goes out on its own: unpushed code commits wait for their own push and release (D86).
    git fetch -q origin main 2>$null
    $ahead = [int](git rev-list --count origin/main..HEAD)
    if (-not $NoPush -and $ahead -gt 1) { Log "learning: committed; other commits are waiting to be pushed, so it goes with them"; return }
    if (-not $NoPush) {
        git push -q origin HEAD:main
        if ($LASTEXITCODE -ne 0) { Log "learning: committed but the push failed; it goes with the next push"; exit 1 }
    }
    Log "learning: published $(git rev-parse --short HEAD)"
}
finally { Pop-Location }
