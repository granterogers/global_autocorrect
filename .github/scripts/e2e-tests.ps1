#Requires -Version 7
# The E2E run itself (CI). Started at medium integrity by Start-MediumIntegrity.ps1; all output goes to
# artifacts/e2e-output.txt, which the workflow prints afterwards. Category=RealApp (the user's own Teams) never runs here.
$ErrorActionPreference = 'Continue'
$out = Join-Path $PWD 'artifacts/e2e-output.txt'
New-Item -ItemType Directory -Force artifacts/test-results | Out-Null
& {
    "integrity: $((whoami /groups | Select-String 'Mandatory Label').ToString().Trim())"
    # D114: a probe run (GA_PROBE=1, "Run workflow" with probe ticked) runs only the write-race probe.
    # D115: editors given ("Run workflow" → editors): only the real-typing test, for those editors.
    $filter = if ($env:GA_PROBE -eq '1') { @('--filter-class', 'GlobalAutocorrect.E2E.WriteRaceProbe') } elseif ($env:GA_RT_EDITORS) { @('--filter-class', 'GlobalAutocorrect.E2E.RealTypingTests') } else { @('--filter-not-trait', 'Category=RealApp', '--filter-not-trait', 'Category=Endurance') }
    dotnet test --project tests/GlobalAutocorrect.E2E --no-build --results-directory "$PWD/artifacts/test-results" --output Detailed `
        -- @filter --report-xunit-trx --report-xunit-markdown --report-xunit-html
    "exit code: $LASTEXITCODE"
} *> $out
exit $LASTEXITCODE
