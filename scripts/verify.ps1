#Requires -Version 7
<#
.SYNOPSIS
  Checks the model runs on the NVIDIA GPU, cleans text and is fast enough (5.8). Each failure says how to fix it.
.EXAMPLE
  pwsh scripts/verify.ps1
#>
param(
    [string] $CacheDir = (Join-Path $env:LOCALAPPDATA 'GlobalAutocorrect\cache'),
    [string] $Model = 'gemma-4-E4B-it-Q5_K_M.gguf',
    [string] $AppExe = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$failures = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()
function Pass([string] $m) { Write-Host "  OK   $m" -ForegroundColor Green }
function Fail([string] $m, [string] $fix) { Write-Host "  FAIL $m" -ForegroundColor Red; Write-Host "       Fix: $fix"; $failures.Add($m) }
function Warn([string] $m) { Write-Host "  NOTE $m" -ForegroundColor Yellow; $warnings.Add($m) }

$manifest = Get-Content (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json
$server = Join-Path $CacheDir "llama\$($manifest.llama.build)\cuda\llama-server.exe"
$modelPath = Join-Path $CacheDir "models\$Model"
Write-Host "Checking Global Autocorrect's model setup"

# 5.8.b
foreach ($scope in 'Process', 'User', 'Machine') {
    $v = [Environment]::GetEnvironmentVariable('CUDA_VISIBLE_DEVICES', $scope)
    if ($v -eq '-1') { Fail "CUDA_VISIBLE_DEVICES is set to -1 ($scope), which hides the GPU." "Remove it: Start > 'Edit environment variables for your account' > delete CUDA_VISIBLE_DEVICES, then sign out and in." }
}
if ($failures.Count -eq 0) { Pass 'CUDA_VISIBLE_DEVICES does not hide the GPU' }

# 5.8.c
$conflict = $env:PATH -split ';' | Where-Object { $_ -and (Test-Path (Join-Path $_ 'cudart64_12.dll')) } | Select-Object -First 1
if ($conflict) { Fail "An older CUDA 12 runtime (cudart64_12.dll) is on PATH in '$conflict'. It can be loaded instead of the CUDA 13 one." "Remove '$conflict' from PATH (Start > 'Edit environment variables'), or uninstall the old CUDA toolkit." }
else { Pass 'No conflicting cudart64_12.dll on PATH' }

if (-not (Test-Path $server) -or -not (Test-Path $modelPath)) {
    Fail 'The model or llama.cpp is not downloaded yet.' 'Run: pwsh scripts/setup.ps1'
    Write-Host "`n$($failures.Count) problem(s) found."; exit 1
}

# 5.8.d
$pref = (Get-ItemProperty 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences' -ErrorAction SilentlyContinue).$server
if ($pref -match 'GpuPreference=2') { Pass 'llama-server is set to use the high-performance GPU' }
else { Fail 'llama-server is not set to the high-performance GPU, so a hybrid laptop may run it on the integrated GPU or CPU.' "Run: pwsh scripts/setup.ps1 -Llama  (or Settings > System > Display > Graphics > add '$server' > High performance)." }

# Start the server the way the app does (D21).
$port = Get-Random -Minimum 20000 -Maximum 60000
$key = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
$log = Join-Path ([IO.Path]::GetTempPath()) "ga-verify-$port.log"
$proc = Start-Process -FilePath $server -PassThru -WindowStyle Hidden -RedirectStandardError $log -RedirectStandardOutput "$log.out" -ArgumentList @(
    '-m', "`"$modelPath`"", '--host', '127.0.0.1', '--port', $port, '-ngl', '99', '-c', '4096', '-np', '1',
    '--reasoning-budget', '0', '-lv', '4', '--no-slots', '--api-key', $key)
try {
    $base = "http://127.0.0.1:$port"
    $up = $false
    for ($i = 0; $i -lt 240 -and -not $up; $i++) {
        if ($proc.HasExited) { break }
        try { $up = (Invoke-WebRequest "$base/health" -TimeoutSec 2).StatusCode -eq 200 } catch { Start-Sleep -Milliseconds 250 }
    }
    $text = Get-Content $log -Raw -ErrorAction SilentlyContinue

    # 5.8.a
    $device = [regex]::Match($text, 'using device (CUDA\d+ \([^)]*\))').Groups[1].Value
    $off = [regex]::Match($text, 'offloaded (\d+)/(\d+) layers to GPU')
    if ($device -and $off.Success -and $off.Groups[1].Value -eq $off.Groups[2].Value) { Pass "Model runs on $device with all $($off.Groups[2].Value) layers offloaded" }
    elseif ($device -and $off.Success) { Fail "Only $($off.Groups[1].Value) of $($off.Groups[2].Value) layers fit on the GPU, so it will be slow." 'Close other GPU-heavy apps, or choose the smaller model in Settings > Clean-up.' }
    else { Fail 'The server log does not show the CUDA device and offloaded layers: it is running on the CPU.' 'Update the NVIDIA driver (GeForce/Studio or RTX Enterprise, 580 or newer), then run: pwsh scripts/setup.ps1 -Llama' }

    # 5.8.e
    $h = @{ Authorization = "Bearer $key" }
    function Clean([string] $t) {
        $body = @{ messages = @(@{ role = 'system'; content = 'Fix typing mistakes. Reply with the corrected text only.' }, @{ role = 'user'; content = $t });
                   temperature = 0; max_tokens = 64; cache_prompt = $true; chat_template_kwargs = @{ enable_thinking = $false } } | ConvertTo-Json -Depth 5
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-RestMethod "$base/v1/chat/completions" -Method Post -Headers $h -ContentType 'application/json' -Body $body -TimeoutSec 30
        return @{ Text = $r.choices[0].message.content; Ms = $sw.Elapsed.TotalMilliseconds }
    }
    if (-not $up) { Fail 'The model did not load.' "Run: pwsh scripts/setup.ps1  to re-download it. Server log: $log" }
    else {
        $r = Clean 'I saw teh cat yesterday'
        if ($r.Text -match '\bthe\b') { Pass 'The model cleans a test sentence' }
        else { Fail "The model did not clean the test sentence (got: $($r.Text))." 'Run: pwsh scripts/setup.ps1  to re-download the model.' }

        # 5.8.f
        $ms = 1..10 | ForEach-Object { (Clean "message number $_ with a typo in teh middle").Ms } | Sort-Object
        $median = [math]::Round(($ms[4] + $ms[5]) / 2)
        if ($median -lt 2000) { Pass "Median clean-up time $median ms (limit 2000 ms)" }
        else { Fail "Median clean-up time is $median ms, at or over the 2 second limit." 'Close other GPU-heavy apps, plug the laptop in, or choose the smaller model in Settings > Clean-up.' }
    }
}
finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}

# 5.8.g (12.10): a keyboard hook plus SendInput can look like a keylogger to antivirus.
try {
    $hits = Get-MpThreatDetection -ErrorAction Stop | Where-Object { ($_.Resources -join ' ') -match 'GlobalAutocorrect' }
    if ($hits) { Fail 'Windows Defender has quarantined or blocked Global Autocorrect.' "Open Windows Security > Virus & threat protection > Protection history, choose the Global Autocorrect item and pick 'Allow on device'. Then add the app folder as an exclusion: Virus & threat protection settings > Exclusions > Add > Folder$(if ($AppExe) { " > '$(Split-Path $AppExe)'" })." }
    else { Pass 'Windows Defender has not blocked the app' }
}
catch { Warn 'Could not read Windows Defender history (another antivirus, or no permission). If the app disappears after starting, see the README section on Defender.' }

Write-Host ''
if ($failures.Count) { Write-Host "$($failures.Count) problem(s) found." -ForegroundColor Red; exit 1 }
Write-Host 'All checks passed.' -ForegroundColor Green
