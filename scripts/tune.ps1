#requires -Version 7
<#
.SYNOPSIS
  D139: weekly automatic tuning of the brain's correction settings (run by .github/workflows/tune.yml on GitHub).

.DESCRIPTION
  Scores the current brain with the real model on a slice of the tuning set (never the holdout, 6.1) and on the
  real-life messages, then each candidate: one setting moved one step either way. A candidate wins only if it fixes at
  least 1 point more of the tuning slice, changes no more correct words, and makes no stupid mistake on the real-life
  messages (D132). The best winner is written to brain/brain.json (with "updated" set); the workflow sends it to the
  brain-candidate branch, where the Brain gate (D138) must pass it before any PC gets it. One step a week, so the brain
  moves slowly and every step is checked.
#>
param([int]$N = 150, [string]$Out = 'artifacts/tune')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null

$steps = [ordered]@{
    'Rules.MaxNormalisedDistance' = 0.05
    'Rules.MaxChangedWordRatio'   = 0.1
    'Rules.MaxNonWordDistance'    = 0.05
    'ContextSentences'            = 1
}
$brain = Get-Content brain/brain.json -Raw | ConvertFrom-Json -AsHashtable
$defaults = @{ 'Rules.MaxNormalisedDistance' = 0.5; 'Rules.MaxChangedWordRatio' = 0.5; 'Rules.MaxNonWordDistance' = 0.65; 'ContextSentences' = 3 }

function Score([string]$name, [hashtable]$b) {
    $dir = Join-Path $Out $name
    New-Item -ItemType Directory -Force $dir | Out-Null
    $path = Join-Path $dir 'brain.json'
    $b | ConvertTo-Json -Depth 32 | Set-Content $path -Encoding utf8NoBOM
    dotnet run --project tools/GlobalAutocorrect.Eval -c Release --no-build -- run --set tuning --n $N --out $dir --timeout 600 --brain $path | Out-Null
    $m = Get-Content (Join-Path $dir 'metrics-tuning.json') -Raw | ConvertFrom-Json
    dotnet run --project tools/GlobalAutocorrect.Eval -c Release --no-build -- run --set reallife --out $dir --timeout 600 --brain $path --strict | Out-Null
    [pscustomobject]@{ Name = $name; Fixed = [double]$m.errorsFixedRate; False = [double]$m.falseEditRate; Stupid = ($LASTEXITCODE -ne 0); Brain = $b }
}

function Clone($h) { $h | ConvertTo-Json -Depth 32 | ConvertFrom-Json -AsHashtable }

$base = Score 'baseline' (Clone $brain)
$results = @($base)
foreach ($knob in $steps.Keys) {
    $now = if ($brain.tuning.ContainsKey($knob)) { [double]$brain.tuning[$knob] } else { [double]$defaults[$knob] }
    foreach ($dir in -1, 1) {
        $value = [math]::Round($now + $dir * $steps[$knob], 2)
        $b = Clone $brain
        if (-not $b.ContainsKey('tuning') -or $null -eq $b.tuning) { $b.tuning = @{} }
        $b.tuning[$knob] = $value
        $results += Score "$knob=$value" $b
    }
}

$table = $results | ForEach-Object { "| $($_.Name) | $([math]::Round($_.Fixed * 100, 1))% | $([math]::Round($_.False * 100, 2))% | $(if ($_.Stupid) { 'yes' } else { 'none' }) |" }
$summary = @('| setting | fixed | false edits | stupid mistakes |', '|---|---|---|---|') + $table
$summary | Set-Content (Join-Path $Out 'summary.md')
$summary | ForEach-Object { $_ }

$winner = $results | Where-Object { $_.Name -ne 'baseline' -and -not $_.Stupid -and $_.False -le $base.False + 0.0005 -and $_.Fixed -ge $base.Fixed + 0.01 } |
    Sort-Object Fixed -Descending | Select-Object -First 1
if (-not $winner) { "tune: no change beats the current brain this week"; return }
$winner.Brain.updated = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$winner.Brain | ConvertTo-Json -Depth 32 | Set-Content brain/brain.json -Encoding utf8NoBOM
"tune: $($winner.Name) fixes $([math]::Round(($winner.Fixed - $base.Fixed) * 100, 1)) points more: written to brain/brain.json"
