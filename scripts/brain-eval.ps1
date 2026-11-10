#requires -Version 7
<#
.SYNOPSIS
  D111: before a brain that changes the rules' thresholds or the AI prompt is pushed, scores it against the brain on
  GitHub (origin/main) on the tuning set (6.1: never the holdout), and passes only if it does at least as well.

.DESCRIPTION
  Runs the real model twice (tools/GlobalAutocorrect.Eval run --set tuning --brain <file>): once with
  brain/brain.json as it is in this checkout, once with origin/main's. Passes when errors fixed are no more than half a
  point lower and false edits no more than 0.2 points higher (a run-to-run margin), and prints both.

  The model needs the GPU to itself, so the installed app is closed while this runs and started again afterwards; this
  waits for 2 minutes without keyboard or mouse first (2.8), unless -Now.
#>
param([switch]$Now, [int]$N = 0)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $env:TEMP "ga-brain-eval"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work | Out-Null

Push-Location $repo
try {
    git fetch -q origin main
    git show origin/main:brain/brain.json | Set-Content (Join-Path $work 'old.json') -Encoding utf8
    Copy-Item brain/brain.json (Join-Path $work 'new.json')
    if ((Get-FileHash (Join-Path $work 'old.json')).Hash -eq (Get-FileHash (Join-Path $work 'new.json')).Hash) { "brain-eval: the brain is the same as origin/main; nothing to score"; exit 0 }

    Add-Type -Namespace BE -Name Idle -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
[DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
public static uint IdleMs() { var i = new LASTINPUTINFO(); i.cbSize = 8; GetLastInputInfo(ref i); return (uint)System.Environment.TickCount - i.dwTime; }
'@
    if (-not $Now) { while ([BE.Idle]::IdleMs() -lt 120000) { Start-Sleep 10 } }

    $installed = Join-Path $env:LOCALAPPDATA 'Programs\GlobalAutocorrect\GlobalAutocorrect.exe'
    $wasRunning = [bool](Get-Process GlobalAutocorrect -ErrorAction SilentlyContinue)
    if ($wasRunning) { & $installed --exit; for ($i = 0; $i -lt 30 -and (Get-Process GlobalAutocorrect -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 } }
    try {
        $scores = @{}
        foreach ($which in 'old', 'new') {
            $out = Join-Path $work $which
            $evalArgs = @('run', '--project', 'tools/GlobalAutocorrect.Eval', '-c', 'Release', '--', 'run', '--set', 'tuning', '--brain', (Join-Path $work "$which.json"), '--out', $out)
            if ($N -gt 0) { $evalArgs += @('--n', "$N") }
            dotnet @evalArgs | Select-Object -Last 2
            if ($LASTEXITCODE -ne 0) { throw "the $which brain's eval failed" }
            $m = Get-ChildItem $out -Filter 'metrics*.json' | Select-Object -First 1
            $scores[$which] = Get-Content $m.FullName -Raw | ConvertFrom-Json
        }
    }
    finally {
        if ($wasRunning -and -not (Get-Process GlobalAutocorrect -ErrorAction SilentlyContinue)) { Start-Process $installed }
    }
    $o = $scores.old; $n = $scores.new
    "brain-eval (tuning set): errors fixed {0:P1} -> {1:P1}; false edits {2:P2} -> {3:P2}" -f $o.errorsFixedRate, $n.errorsFixedRate, $o.falseEditRate, $n.falseEditRate
    if ($n.errorsFixedRate -lt $o.errorsFixedRate - 0.005 -or $n.falseEditRate -gt $o.falseEditRate + 0.002) { "brain-eval: FAIL, the new brain scores worse: don't push it"; exit 1 }
    "brain-eval: PASS"
}
finally { Pop-Location }
