#Requires -Version 7
<#
.SYNOPSIS
  Downloads the pinned llama.cpp build, CUDA runtime, model, dictionary and corpus, with SHA-256 checks (5.7).
.EXAMPLE
  pwsh scripts/setup.ps1                       # llama.cpp (CUDA + CPU) and the default model
  pwsh scripts/setup.ps1 -Model qwen35-4b-q5   # a specific model from scripts/manifest.json
  pwsh scripts/setup.ps1 -Dictionary -Corpus   # build-time data only
#>
param(
    [string[]] $Model = @(),
    [switch] $Llama,
    [switch] $Dictionary,
    [switch] $Corpus,
    [string] $CacheDir = (Join-Path $env:LOCALAPPDATA 'GlobalAutocorrect\cache')
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
$defaultModel = 'gemma4-e4b-q5'   # replaced by the Spike A winner (5.3.d)

# No switches means a normal install: runtime plus the default model.
if (-not ($Model -or $Llama -or $Dictionary -or $Corpus)) { $Llama = $true; $Model = @($defaultModel) }

function Get-Verified([string] $Url, [string] $Dest, [string] $Sha256) {
    if ((Test-Path $Dest) -and $Sha256 -and ((Get-FileHash $Dest -Algorithm SHA256).Hash -ieq $Sha256)) {
        Write-Host "  already downloaded: $(Split-Path $Dest -Leaf)"
        return
    }
    New-Item -ItemType Directory -Force (Split-Path $Dest) | Out-Null
    Write-Host "  downloading $(Split-Path $Dest -Leaf)"
    # curl.exe resumes partial downloads (-C -) and fails on HTTP errors.
    & curl.exe -L --fail --retry 3 -C - -o $Dest $Url
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $Url (curl exit $LASTEXITCODE). Check the internet connection and run this again; it resumes." }
    $actual = (Get-FileHash $Dest -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $Sha256) {
        Write-Host "  no pinned checksum; sha256 = $actual (record it in scripts/manifest.json)"
    } elseif ($actual -ine $Sha256) {
        Remove-Item $Dest -Force
        throw "Checksum mismatch for $(Split-Path $Dest -Leaf). Expected $Sha256, got $actual. The file was deleted; run this again."
    }
}

function Expand-Into([string] $Zip, [string] $Dir) {
    New-Item -ItemType Directory -Force $Dir | Out-Null
    Expand-Archive -Path $Zip -DestinationPath $Dir -Force
}

if ($Llama) {
    $b = $manifest.llama.build
    Write-Host "llama.cpp $b"
    $dl = Join-Path $CacheDir 'downloads'
    foreach ($k in 'cuda', 'cudart', 'cpu') {
        $e = $manifest.llama[$k]
        Get-Verified $e.url (Join-Path $dl (Split-Path $e.url -Leaf)) $e.sha256
    }
    # 5.1.a: the CUDA build and its matching cudart DLLs live in the same folder.
    $cudaDir = Join-Path $CacheDir "llama\$b\cuda"
    Expand-Into (Join-Path $dl (Split-Path $manifest.llama.cuda.url -Leaf)) $cudaDir
    Expand-Into (Join-Path $dl (Split-Path $manifest.llama.cudart.url -Leaf)) $cudaDir
    Expand-Into (Join-Path $dl (Split-Path $manifest.llama.cpu.url -Leaf)) (Join-Path $CacheDir "llama\$b\cpu")

    # 5.8.d: hybrid graphics laptops must run llama-server on the NVIDIA GPU.
    $exe = Get-ChildItem $cudaDir -Recurse -Filter llama-server.exe | Select-Object -First 1
    if (-not $exe) { throw "llama-server.exe not found in $cudaDir after extracting." }
    $key = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name $exe.FullName -Value 'GpuPreference=2;' -PropertyType String -Force | Out-Null
    Write-Host "  llama-server: $($exe.FullName) (set to high-performance GPU)"
}

foreach ($m in $Model) {
    if (-not $manifest.models.ContainsKey($m)) { throw "Unknown model '$m'. Known: $($manifest.models.Keys -join ', ')" }
    $e = $manifest.models[$m]
    Write-Host "model $m"
    Get-Verified $e.url (Join-Path $CacheDir "models\$($e.file)") $e.sha256
}

if ($Dictionary) {
    Write-Host 'dictionary en_GB-ise'
    $zip = Join-Path $CacheDir "downloads\$(Split-Path $manifest.dictionary.url -Leaf)"
    Get-Verified $manifest.dictionary.url $zip $manifest.dictionary.sha256
    $tmp = Join-Path $CacheDir 'dictionary'
    Expand-Into $zip $tmp
    $assets = Join-Path $repo 'assets'
    New-Item -ItemType Directory -Force $assets | Out-Null
    foreach ($ext in 'dic', 'aff') {
        $f = Get-ChildItem $tmp -Recurse -Filter "en_GB-ise.$ext" | Select-Object -First 1
        if (-not $f) { throw "en_GB-ise.$ext not found in the dictionary zip." }
        Copy-Item $f.FullName (Join-Path $assets "en_GB-ise.$ext") -Force
    }
    Write-Host "  copied en_GB-ise.dic/.aff to assets/"
}

if ($Corpus) {
    Write-Host 'corpus (Leipzig, CC BY)'
    $tgz = Join-Path $CacheDir "corpus\$(Split-Path $manifest.corpus.url -Leaf)"
    Get-Verified $manifest.corpus.url $tgz $manifest.corpus.sha256
    & tar.exe -xzf $tgz -C (Split-Path $tgz)
    if ($LASTEXITCODE -ne 0) { throw "Could not extract $tgz." }
    Write-Host "  extracted to $(Split-Path $tgz)"
}

Write-Host 'Setup finished.'
