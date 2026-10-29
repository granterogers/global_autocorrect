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
    Invoke-Step 'test (Core)' {
        dotnet test --project tests/GlobalAutocorrect.Core.Tests --no-build -- --filter-not-trait "Category=Model" --filter-not-trait "Category=E2E"
    }
    Invoke-Step 'test (Llm)' {
        dotnet test --project tests/GlobalAutocorrect.Llm.Tests --no-build -- --filter-not-trait "Category=Model"
    }
    Invoke-Step 'test (Windows)' {
        dotnet test --project tests/GlobalAutocorrect.Windows.Tests --no-build -- --filter-not-trait "Category=E2E"
    }
    Invoke-Step 'test (App)' {
        dotnet test --project tests/GlobalAutocorrect.App.Tests --no-build
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
