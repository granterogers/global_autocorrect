#Requires -Version 7
<#
.SYNOPSIS
  Requirement coverage (2.11, 11.2): for each ID in PLAN.md §9, finds the tests whose names carry it, or the recorded
  check. Writes artifacts/coverage.md (the README table) and prints any ID with neither.
#>
param([string] $Out = 'artifacts/coverage.md', [string] $E2EResults = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    # Every test method name in the repo, e.g. R4_5_a_AcceptsCharacterSimilarWord.
    $tests = Get-ChildItem tests -Recurse -Filter *.cs | Where-Object FullName -notmatch '\\(bin|obj)\\' |
        Select-String -Pattern '(?:void|Task)\s+(R\d+_\d+(?:_[a-z])?_\w+)\s*\(' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    $e2e = Get-ChildItem tests/GlobalAutocorrect.E2E -Recurse -Filter *.cs | Where-Object FullName -notmatch '\\(bin|obj)\\' |
        Select-String -Pattern '(?:void|Task)\s+(R\w+)\s*\(' -AllMatches | ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value }

    $rows = [System.Collections.Generic.List[string]]::new()
    $gaps = [System.Collections.Generic.List[string]]::new()
    $plan = Get-Content PLAN.md
    $inTable = $false
    foreach ($line in $plan) {
        if ($line -match '^## 9\.') { $inTable = $true; continue }
        if (-not $inTable -or $line -notmatch '^\|\s*(\d+\.\d+(?:\.[a-z])?)\s*\|\s*([^|]*)\|\s*(.*)\|\s*$') { continue }
        $id = $Matches[1]; $proof = $Matches[3].Trim()
        $prefix = 'R' + ($id -replace '\.', '_') + '_'
        # A test "carries" an ID if its name starts with it, names it later (..._R4_6_b or ..._R4_6_b_...), or is one
        # of the test prefixes PLAN §9 gives as proof for that ID (`R4_4_c_*`).
        $listed = [regex]::Matches($proof, '`(R[\w]+?)_?\*?`') | ForEach-Object { $_.Groups[1].Value.TrimEnd('_') + '_' }
        $hits = @($tests | Where-Object {
            $t = $_ + '_'
            $t.StartsWith($prefix) -or $t.Contains('_' + $prefix) -or ($listed | Where-Object { $t.StartsWith($_) })
        })
        if ($hits.Count -gt 0) {
            $kind = if ($hits | Where-Object { $e2e -contains $_ }) { 'Tested (incl. E2E)' } else { 'Tested' }
            $rows.Add("| $id | $kind | $($hits.Count) test(s), e.g. ``$($hits[0])`` |")
        } elseif ($proof -match '^Check:|^Out of scope') {
            $rows.Add("| $id | $(if ($proof -match '^Out') { 'Out of scope (14)' } else { 'Recorded check' }) | $($proof -replace '^Check:\s*','') |")
        } else {
            $rows.Add("| $id | **Gap** | planned: $proof |")
            $gaps.Add($id)
        }
    }
    New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
    @('| ID | Status | Proof |', '|---|---|---|') + $rows | Set-Content $Out -Encoding utf8
    Write-Host "coverage: $($rows.Count) IDs, $($gaps.Count) gaps -> $Out"
    if ($gaps.Count) { Write-Host ('gaps: ' + ($gaps -join ', ')) }
}
finally { Pop-Location }
