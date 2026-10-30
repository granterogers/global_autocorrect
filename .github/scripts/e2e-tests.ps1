#Requires -Version 7
# The E2E run itself (CI). Started at medium integrity by Start-MediumIntegrity.ps1; all output goes to
# artifacts/e2e-output.txt, which the workflow prints afterwards. Category=RealApp (the user's own Teams) never runs here.
$ErrorActionPreference = 'Continue'
$out = Join-Path $PWD 'artifacts/e2e-output.txt'
New-Item -ItemType Directory -Force artifacts/test-results | Out-Null
& {
    "integrity: $((whoami /groups | Select-String 'Mandatory Label').ToString().Trim())"
    dotnet test --project tests/GlobalAutocorrect.E2E --no-build --results-directory "$PWD/artifacts/test-results" --output Detailed `
        -- --filter-not-trait "Category=RealApp" --report-xunit-trx --report-xunit-markdown --report-xunit-html
    "exit code: $LASTEXITCODE"
} *> $out
exit $LASTEXITCODE
