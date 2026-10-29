#Requires -Version 7
# Verifies every requirement ID in BRIEF.md appears as a table cell in PLAN.md (2.3.c, 11.2).
param(
    [string]$Plan = 'PLAN.md',
    [string]$Brief = 'BRIEF.md'
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($Plan))  { $Plan  = Join-Path $root $Plan }
if (-not [System.IO.Path]::IsPathRooted($Brief)) { $Brief = Join-Path $root $Brief }

$idPattern = '^\s*(?:- )?(\d+\.\d+(?:\.[a-z])?)\s'
$ids = Get-Content -LiteralPath $Brief |
    ForEach-Object { if ($_ -match $idPattern) { $Matches[1] } } |
    Select-Object -Unique

$planText = Get-Content -LiteralPath $Plan -Raw
$missing = @($ids | Where-Object { $planText -notmatch ('\|\s*' + [regex]::Escape($_) + '\s*\|') })

if ($missing.Count -gt 0) {
    $missing | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "trace-check: $(@($ids).Count) IDs, all present"
exit 0
