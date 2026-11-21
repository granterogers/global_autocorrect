#requires -Version 7
<#
.SYNOPSIS
  D139, D165: weekly automatic tuning of the brain's correction, suggestion and placement settings (run by .github/workflows/tune.yml on GitHub).

.DESCRIPTION
  Scores the current brain with the real model on a slice of the tuning set (never the holdout, 6.1) and on the
  real-life messages, then each candidate: one setting moved one step either way. A candidate wins only if it fixes at
  least 1 point more of the tuning slice, changes no more correct words, and makes no stupid mistake on the real-life
  messages (D132). The best winner is written to brain/brain.json (with "updated" set); the workflow sends it to the
  brain-candidate branch, where the Brain gate (D138) must pass it before any PC gets it. One step a week, so the brain
  moves slowly and every step is checked.
#>
param([int]$N = 150, [string]$Out = 'artifacts/tune', [switch]$CountsOnly)   # D165: -CountsOnly skips the model runs (only the count-based steps)
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

$results = @()
if (-not $CountsOnly) {
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

}

$table = $results | ForEach-Object { "| $($_.Name) | $([math]::Round($_.Fixed * 100, 1))% | $([math]::Round($_.False * 100, 2))% | $(if ($_.Stupid) { 'yes' } else { 'none' }) |" }
$summary = @('| setting | fixed | false edits | stupid mistakes |', '|---|---|---|---|') + $table
$summary | Set-Content (Join-Path $Out 'summary.md')
$summary | ForEach-Object { $_ }

$winner = if ($CountsOnly) { $null } else { $results | Where-Object { $_.Name -ne 'baseline' -and -not $_.Stupid -and $_.False -le $base.False + 0.0005 -and $_.Fixed -ge $base.Fixed + 0.01 } |
    Sort-Object Fixed -Descending | Select-Object -First 1 }
$next = if ($winner) { $winner.Brain } else { Clone $brain }
$notes = @()
if ($winner) { $notes += "$($winner.Name) fixes $([math]::Round(($winner.Fixed - $base.Fixed) * 100, 1)) points more" }

# D165 (user: "is it constantly improving itself?"): settings learnt from how the app does on everyone's PCs, from the
# counts the brain already holds (numbers only, D103). One step a week each, then the Brain gate as for every change.
if (-not $next.ContainsKey('tuning') -or $null -eq $next.tuning) { $next.tuning = @{} }
function Total([string]$field) {
    $sum = 0.0
    foreach ($app in @($brain.apps.Values)) { if ($app -and $app.ContainsKey($field)) { $sum += [double]$app[$field] } }
    $sum
}
function Nudge([string]$knob, [double]$default, [double]$step, [double]$min, [double]$max, [int]$direction, [string]$why) {
    $now = if ($next.tuning.ContainsKey($knob)) { [double]$next.tuning[$knob] } else { $default }
    $value = [math]::Round([math]::Min($max, [math]::Max($min, $now + $direction * $step)), 2)
    if ($value -ne $now) { $next.tuning[$knob] = $value; $script:notes += "$knob $now -> $value ($why)" }
}
# Suggestions taken with Tab: few taken means too many shown (raise the bar); many taken, room to show more.
foreach ($k in @(
    @{ Shown = 'suggestShown'; Taken = 'suggestTaken'; Knob = 'Predict.ModelMinChance'; Default = 0.15 },
    @{ Shown = 'nextShown'; Taken = 'nextTaken'; Knob = 'Predict.ModelNextMinChance'; Default = 0.7 })) {
    $shown = Total $k.Shown; $taken = Total $k.Taken
    if ($shown -lt 200) { continue }
    $rate = $taken / $shown
    if ($rate -lt 0.2) { Nudge $k.Knob $k.Default 0.05 0.1 0.95 1 "only $([math]::Round($rate * 100))% of $([math]::Round($shown)) taken" }
    elseif ($rate -gt 0.45) { Nudge $k.Knob $k.Default 0.05 0.1 0.95 -1 "$([math]::Round($rate * 100))% of $([math]::Round($shown)) taken" }
}
# Placed fixes the field wouldn't show selected (D162): often, so give it longer; rarely, edge back towards the default.
$writes = Total 'writes'; $refused = Total 'placeRefusals'
if ($writes -ge 200) {
    $share = $refused / $writes
    if ($share -gt 0.05) { Nudge 'Place.SelectWaitMs' 400 100 100 1000 1 "$([math]::Round($share * 100, 1))% of fixes refused" }
    elseif ($share -lt 0.01 -and $next.tuning.ContainsKey('Place.SelectWaitMs') -and [double]$next.tuning['Place.SelectWaitMs'] -gt 400) { Nudge 'Place.SelectWaitMs' 400 50 400 1000 -1 "refusals rare" }
}

if ($notes.Count -eq 0) { "tune: no change beats the current brain this week"; return }
$next.updated = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$next | ConvertTo-Json -Depth 32 | Set-Content brain/brain.json -Encoding utf8NoBOM
$notes | ForEach-Object { "tune: $_" }
"tune: written to brain/brain.json"
