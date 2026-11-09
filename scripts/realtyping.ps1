#requires -Version 7
<#
.SYNOPSIS
  D104: runs the real-typing screen test (tests/GlobalAutocorrect.E2E/RealTypingTests.cs) on this PC once the user has
  been away from the keyboard and mouse for 2 minutes (2.8), and puts the installed app back afterwards.

.DESCRIPTION
  Waits up to -WaitHours for 2 minutes of no input. Then closes the installed copy cleanly (only one copy can run),
  runs the test against a copy of the Debug build with the model stand-in, and starts the installed copy again whatever
  happened. Results: artifacts/realtyping/summary.md (one row per editor) and *-damage.txt for any damaged words.
#>
param([double]$WaitHours = 8, [string]$Filter = 'GlobalAutocorrect.E2E.RealTypingTests', [switch]$Eval)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$installed = Join-Path $env:LOCALAPPDATA 'Programs\GlobalAutocorrect\GlobalAutocorrect.exe'
$out = Join-Path $repo 'artifacts\realtyping'

Add-Type -Namespace RT -Name Idle -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
[DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
public static uint IdleMs() { var i = new LASTINPUTINFO(); i.cbSize = 8; GetLastInputInfo(ref i); return (uint)System.Environment.TickCount - i.dwTime; }
'@

$deadline = (Get-Date).AddHours($WaitHours)
while ([RT.Idle]::IdleMs() -lt 120000) {
    if ((Get-Date) -gt $deadline) { "realtyping: the user was never away for 2 minutes; not run"; exit 2 }
    Start-Sleep -Seconds 10
}
"realtyping: user away since $((Get-Date).AddMilliseconds(-[RT.Idle]::IdleMs()).ToString('HH:mm:ss')); starting"

Push-Location $repo
$wasRunning = [bool](Get-Process GlobalAutocorrect -ErrorAction SilentlyContinue)
try {
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    $app = Join-Path $env:TEMP "ga-realtyping-app"
    if (Test-Path $app) { Remove-Item $app -Recurse -Force }
    Copy-Item (Join-Path $repo 'src\GlobalAutocorrect.App\bin\Debug\net10.0-windows') $app -Recurse
    if ($wasRunning -and (Test-Path $installed)) {
        & $installed --exit
        for ($i = 0; $i -lt 30 -and (Get-Process GlobalAutocorrect -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    }
    $env:GA_E2E = '1'; $env:GA_E2E_APP = $app; $env:GA_REPO = $repo
    dotnet test --project tests/GlobalAutocorrect.E2E --no-build -- --filter-class $Filter 2>&1 | Tee-Object -FilePath (New-Item -ItemType Directory -Force $out | Join-Path -ChildPath 'run.log')
    $code = $LASTEXITCODE
    # -Eval: score accuracy (6.3) while the installed copy is closed, so the model has the GPU to itself.
    if ($Eval) {
        Remove-Item Env:GA_E2E -ErrorAction SilentlyContinue
        foreach ($set in 'tuning', 'holdout') {
            dotnet run --project tools/GlobalAutocorrect.Eval -c Release -- run --set $set --out artifacts 2>&1 | Select-Object -Last 3 | Tee-Object -FilePath (Join-Path $out 'eval.log') -Append
        }
    }
}
finally {
    Remove-Item Env:GA_E2E, Env:GA_E2E_APP -ErrorAction SilentlyContinue
    if ($wasRunning -and (Test-Path $installed) -and -not (Get-Process GlobalAutocorrect -ErrorAction SilentlyContinue)) { Start-Process $installed }
    Pop-Location
}
if (Test-Path (Join-Path $out 'summary.md')) { Get-Content (Join-Path $out 'summary.md') }
exit $code
