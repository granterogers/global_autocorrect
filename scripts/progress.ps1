#Requires -Version 7
<#
.SYNOPSIS
  Live progress page for the release work: writes artifacts/progress.html every few seconds (it refreshes itself).
  Steps come from artifacts/progress.json (updated by the lead engineer); everything else is read live.
.EXAMPLE
  pwsh scripts/progress.ps1 -E2ELog <path to the running E2E output file>
#>
param([string] $E2ELog = '', [int] $Seconds = 3)
$ErrorActionPreference = 'SilentlyContinue'
$repo = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repo 'artifacts/progress.html'
$stepsFile = Join-Path $repo 'artifacts/progress.json'
Add-Type -Namespace GaProgress -Name Idle -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct LII { public uint cbSize; public uint dwTime; }
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LII p);
'@
function Esc([string] $s) { [System.Net.WebUtility]::HtmlEncode($s) }

while ($true) {
    $lii = New-Object GaProgress.Idle+LII; $lii.cbSize = 8; [void][GaProgress.Idle]::GetLastInputInfo([ref]$lii)
    $idle = [int](([Environment]::TickCount - $lii.dwTime) / 1000)
    $state = if (Test-Path $stepsFile) { Get-Content $stepsFile -Raw | ConvertFrom-Json } else { $null }
    if ($state.e2eLog) { $E2ELog = $state.e2eLog }

    # E2E results as they finish (xunit prints [PASS]/[FAIL]/[SKIP] per test).
    $tests = @(); $running = $false
    if ($E2ELog -and (Test-Path $E2ELog)) {
        $lines = Get-Content $E2ELog
        foreach ($l in $lines) {
            if ($l -match '^\s+GlobalAutocorrect\.E2E\.(\w+)\.(\S+?)(\(.*\))?\s+\[(PASS|FAIL|SKIP)\]') {
                $tests += [pscustomobject]@{ Name = "$($Matches[2])$($Matches[3])"; Result = $Matches[4]; Detail = '' }
            } elseif ($tests.Count -and $tests[-1].Result -eq 'FAIL' -and $tests[-1].Detail.Length -lt 300 -and $l.Trim()) {
                $tests[-1].Detail += ' ' + $l.Trim()
            }
        }
        $running = -not ($lines -match 'Total:\s*\d+')
    }
    $appUp = [bool](Get-Process GlobalAutocorrect)
    $pass = ($tests | Where-Object Result -eq 'PASS').Count; $fail = ($tests | Where-Object Result -eq 'FAIL').Count

    $steps = @($state.steps); [array]::Reverse($steps)
    $now = $state.steps | Where-Object status -eq 'active' | Select-Object -First 1
    $stepsHtml = ($steps | ForEach-Object {
        $icon = switch ($_.status) { 'done' { '&#10003;' } 'active' { '&#9679;' } 'blocked' { '!' } default { '' } }
        "<li class='$($_.status)'><span class='dot'>$icon</span><div><b>$(Esc $_.title)</b>$(if ($_.note) { "<small>$(Esc $_.note)</small>" })</div></li>"
    }) -join "`n"
    $newest = @($tests); [array]::Reverse($newest)
    $testsHtml = if ($tests.Count) { ($newest | ForEach-Object { "<tr class='$($_.Result.ToLower())'><td>$($_.Result)</td><td>$(Esc $_.Name)<small>$(Esc $_.Detail)</small></td></tr>" }) -join "`n" } else { "<tr><td colspan='2' class='muted'>No results yet</td></tr>" }
    $commits = (git -C $repo log --format='%h|%cr|%s' -8) | ForEach-Object { $p = $_ -split '\|', 3; "<li><code>$($p[0])</code> $(Esc $p[2]) <small>$($p[1])</small></li>" }
    $idlePct = [Math]::Min(100, [int]($idle / 120 * 100))
    $e2eState = if (-not $E2ELog) { 'Not started' } elseif ($running) { if ($idle -lt 120) { "Waiting for you to step away: $idle of 120 s idle" } else { 'Running: typing into apps now, please don''t touch the keyboard' } } else { 'Finished' }

    $html = @"
<!doctype html><html lang="en-GB"><head><meta charset="utf-8"><meta http-equiv="refresh" content="$Seconds">
<title>Global Autocorrect RC1 progress</title><meta name="viewport" content="width=device-width, initial-scale=1">
<style>
:root{--bg:#F7F9FC;--card:#fff;--line:#E3E8F0;--text:#0B1220;--muted:#4A5568;--accent:#1F6FEB;--ok:#1a7f37;--bad:#c62828;--warn:#b26a00}
@media (prefers-color-scheme:dark){:root{--bg:#0F1622;--card:#172131;--line:#26324A;--text:#E8EEF8;--muted:#A9B6CC;--accent:#5B9BFF;--ok:#4cc26b;--bad:#ff6b6b;--warn:#f0b44c}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 "Segoe UI Variable Text","Segoe UI",sans-serif}
main{max-width:980px;margin:0 auto;padding:20px 16px 40px}h1{font-size:22px;margin:0 0 4px}h2{font-size:15px;margin:0 0 10px}
.sub{color:var(--muted);margin:0 0 18px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:14px}
.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px}
ul{list-style:none;margin:0;padding:0}li{display:flex;gap:10px;padding:6px 0;border-bottom:1px solid var(--line)}li:last-child{border:0}
.dot{flex:0 0 22px;height:22px;border-radius:50%;border:2px solid var(--line);display:grid;place-items:center;font-size:12px;color:#fff}
.done .dot{background:var(--ok);border-color:var(--ok)}.active .dot{border-color:var(--accent);color:var(--accent)}.blocked .dot{background:var(--warn);border-color:var(--warn)}
li small,td small{display:block;color:var(--muted)}.big{font-size:28px;font-weight:600}.muted{color:var(--muted)}
.bar{height:8px;background:var(--line);border-radius:4px;overflow:hidden;margin:8px 0}.bar i{display:block;height:100%;background:var(--accent);width:$idlePct%}
table{width:100%;border-collapse:collapse}td{padding:5px 6px;border-bottom:1px solid var(--line);vertical-align:top}td:first-child{width:52px;font-weight:600}
.now{background:var(--card);border:2px solid var(--accent);border-radius:10px;padding:14px 16px;margin:0 0 14px}.now b{font-size:16px}
.pass td:first-child{color:var(--ok)}.fail td:first-child{color:var(--bad)}.skip td:first-child{color:var(--warn)}code{font-size:12px}
</style></head><body><main>
<h1>Global Autocorrect: release candidate 1</h1>
<p class="sub">Live, updated every $Seconds s &middot; $(Get-Date -Format 'HH:mm:ss') &middot; app running: $(if ($appUp) {'yes'} else {'no'})</p>
<section class="now"><small class="muted">Right now</small><br><b>$(Esc $now.title)</b><br>$(Esc $now.note)<br><span class="muted">End-to-end: $(Esc $e2eState) &middot; $pass passed, $fail failed</span></section>
<div class="grid">
<section class="card"><h2>End-to-end tests in real apps (newest first)</h2>
<div class="big">$pass passed &middot; $fail failed</div><p class="muted">$(Esc $e2eState)</p>
<div class="bar"><i></i></div><small class="muted">These tests type into Notepad, a WPF box and Edge, so they wait until the keyboard and mouse have been idle for 2 minutes.</small>
<table>$testsHtml</table></section>
<section class="card"><h2>Release steps (newest first)</h2><ul>$stepsHtml</ul></section>
<section class="card"><h2>Latest commits</h2><ul>$($commits -join '')</ul></section>
</div></main></body></html>
"@
    [IO.File]::WriteAllText($out, $html, [Text.UTF8Encoding]::new($false))
    Start-Sleep -Seconds $Seconds
}
