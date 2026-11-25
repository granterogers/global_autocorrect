#Requires -Version 7
# Single entry point for all checks (11.1.a-e). Usage: pwsh ./check.ps1 [fast|model|eval|e2e|full]
param([ValidateSet('fast','model','eval','e2e','full')][string]$Tier = 'fast')

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot

$script:CurrentStep = '(start)'

function Invoke-Step($name, [scriptblock]$cmd) {
    $script:CurrentStep = $name
    Write-Host "==> $name" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $cmd
    $sw.Stop()
    $code = $LASTEXITCODE
    Write-Host ("    {0}: {1:N1}s" -f $name, $sw.Elapsed.TotalSeconds)
    if ($code -ne 0) { throw "step '$name' failed with exit code $code" }
}

function Invoke-Build {
    Invoke-Step 'build' { dotnet build GlobalAutocorrect.slnx --no-restore -v q --nologo }
}

function Invoke-Fast {
    Invoke-Step 'trace-check' { pwsh -NoProfile -File scripts/trace-check.ps1 }
    if (-not (Test-Path 'src/GlobalAutocorrect.Core/obj/project.assets.json')) {
        Invoke-Step 'restore' { dotnet restore GlobalAutocorrect.slnx }
    }
    Invoke-Build
    # D186 (11.1.a, under 30 s): the four test projects at once, each still running its own tests side by side. One after
    # another they took 70 s. A project that fails shows its whole output; one that passes, a line.
    $projects = [ordered]@{
        'test (Core)'    = @('tests/GlobalAutocorrect.Core.Tests', '--filter-not-trait', 'Category=Model', '--filter-not-trait', 'Category=E2E')
        'test (Llm)'     = @('tests/GlobalAutocorrect.Llm.Tests', '--filter-not-trait', 'Category=Model')
        'test (Windows)' = @('tests/GlobalAutocorrect.Windows.Tests', '--filter-not-trait', 'Category=E2E')
        'test (App)'     = @('tests/GlobalAutocorrect.App.Tests')
    }
    Invoke-Step 'tests (4 projects at once)' {
        $root = (Get-Location).Path
        $jobs = foreach ($name in $projects.Keys) {
            Start-ThreadJob -Name $name -ArgumentList $root, $projects[$name] -ScriptBlock {
                param($root, [string[]]$a)
                Set-Location $root
                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                $rest = @($a | Select-Object -Skip 1)
                $out = if ($rest.Count) { dotnet test --project $a[0] --no-build -- @rest 2>&1 | Out-String } else { dotnet test --project $a[0] --no-build 2>&1 | Out-String }
                [pscustomobject]@{ Code = $LASTEXITCODE; Out = $out; Secs = $sw.Elapsed.TotalSeconds }
            }
        }
        $failed = @()
        foreach ($j in $jobs) {
            $r = Receive-Job $j -Wait -AutoRemoveJob
            $summary = "$($r.Out -split "`n" | Where-Object { $_ -match '^\s*total:\s*\d+' } | Select-Object -Last 1)".Trim()
            if ($r.Code -ne 0) { $failed += $j.Name; Write-Host $r.Out }
            Write-Host ("    {0}: {1:N1}s, {2}{3}" -f $j.Name, $r.Secs, $summary, $(if ($r.Code -ne 0) { ' FAILED' } else { '' }))
        }
        if ($failed) { $script:CurrentStep = $failed -join ', ' }
        $global:LASTEXITCODE = [int]($failed.Count -gt 0)
    }
}

function Invoke-Model {
    Invoke-Build
    Invoke-Step 'test (Llm)' { dotnet test --project tests/GlobalAutocorrect.Llm.Tests --no-build -- --filter-trait "Category=Model" }
}

function Invoke-Eval {
    Invoke-Step 'eval (holdout)' {
        dotnet run --project tools/GlobalAutocorrect.Eval -c Release -- run --set holdout --out artifacts
    }
}

function Invoke-E2E {
    Invoke-Build
    try {
        $env:GA_E2E = '1'
        Invoke-Step 'test (E2E)' { dotnet test --project tests/GlobalAutocorrect.E2E --no-build }
    }
    finally {
        Remove-Item Env:GA_E2E -ErrorAction SilentlyContinue
    }
}

$total = [System.Diagnostics.Stopwatch]::StartNew()
try {
    switch ($Tier) {
        'fast'  { Invoke-Fast }
        'model' { Invoke-Model }
        'eval'  { Invoke-Eval }
        'e2e'   { Invoke-E2E }
        'full'  {
            Invoke-Fast
            # D61: the random-typing test at length (the fast tier runs 300 sessions).
            Invoke-Step 'random typing (20,000 sessions)' {
                $env:GA_FUZZ_SESSIONS = '20000'
                try { dotnet test --project tests/GlobalAutocorrect.Core.Tests --no-build -- --filter-method "*RandomTypingNeverDamagesTheField*" }
                finally { $env:GA_FUZZ_SESSIONS = $null }
            }
            # D70: the layout checks at 200% as well as 100% (the fast tier checks 100% only).
            Invoke-Step 'layout checks at 100% and 200%' {
                $env:GA_LAYOUT_SCALES = '100,200'
                try { dotnet test --project tests/GlobalAutocorrect.App.Tests --no-build -- --filter-class "*LayoutTests" }
                finally { $env:GA_LAYOUT_SCALES = $null }
            }
            Invoke-Model
            Invoke-Eval
            Invoke-E2E
            Invoke-Step 'package' {
                if (Test-Path 'scripts/package.ps1') {
                    pwsh -NoProfile -File scripts/package.ps1
                }
                else {
                    Write-Host 'package.ps1 not yet written'
                    $global:LASTEXITCODE = 1
                }
            }
        }
    }
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "${Tier}: FAIL at $script:CurrentStep" -ForegroundColor Red
    Pop-Location
    exit 1
}
$total.Stop()
Pop-Location

$secs = $total.Elapsed.TotalSeconds
Write-Host ("{0}: PASS in {1:N1}s" -f $Tier, $secs) -ForegroundColor Green
if ($Tier -eq 'fast' -and $secs -gt 30) {
    Write-Host 'fast tier exceeded 30 s budget (11.1.a)' -ForegroundColor Yellow
}
exit 0
