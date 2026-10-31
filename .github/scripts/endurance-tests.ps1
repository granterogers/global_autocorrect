#Requires -Version 7
# The endurance run itself (CI, D72). Started at medium integrity by Start-MediumIntegrity.ps1 with GA_ENDURANCE_MINUTES
# set; all output goes to artifacts/endurance-output.txt, which the workflow prints afterwards.
$ErrorActionPreference = 'Continue'
$out = Join-Path $PWD 'artifacts/endurance-output.txt'
New-Item -ItemType Directory -Force artifacts/endurance | Out-Null
& {
    "integrity: $((whoami /groups | Select-String 'Mandatory Label').ToString().Trim()); minutes: $env:GA_ENDURANCE_MINUTES"
    dotnet test --project tests/GlobalAutocorrect.E2E --no-build --output Detailed -- --filter-trait "Category=Endurance"
    "exit code: $LASTEXITCODE"
} *> $out
exit $LASTEXITCODE
