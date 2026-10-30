#Requires -Version 7
<#
.SYNOPSIS
  Installs or updates Global Autocorrect for this Windows user, then starts it. Safe to run again: an update closes the
  running copy cleanly (as the tray's Exit does), replaces the files and starts the new one.
.DESCRIPTION
  Run from the unzipped package (dist\GlobalAutocorrect in the repo). Steps:
    1. Model and llama.cpp: downloaded with checksums only if missing (scripts\setup.ps1, 5.7).
    2. Close the running copy cleanly.
    3. Copy the app to %LOCALAPPDATA%\Programs\GlobalAutocorrect.
    4. Start it, not elevated (10.8). The app registers itself to start with Windows (8.1).
  Settings, the personal dictionary and history live in %APPDATA%\GlobalAutocorrect and are never touched.
.EXAMPLE
  pwsh -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
#>
param(
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\GlobalAutocorrect'),
    [switch] $NoStart,
    [switch] $Setup      # run the setup checks even when the model and llama.cpp are already there
)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$cache = Join-Path $env:LOCALAPPDATA 'GlobalAutocorrect\cache'
$manifest = Get-Content (Join-Path $src 'scripts\manifest.json') -Raw | ConvertFrom-Json
$build = if (Test-Path (Join-Path $src 'build.txt')) { (Get-Content (Join-Path $src 'build.txt') -Raw).Trim() } else { 'unknown build' }
Write-Host "Global Autocorrect: installing $build"

# 1. Runtime and model. setup.ps1 re-hashes everything it checks, so only call it when something is missing.
$llama = Join-Path $cache "llama\$($manifest.llama.build)\cuda"
$model = Join-Path $cache "models\$($manifest.models.'gemma4-e4b-q5'.file)"
$haveLlama = (Test-Path $llama) -and (Get-ChildItem $llama -Recurse -Filter llama-server.exe -ErrorAction SilentlyContinue | Select-Object -First 1)
if ($Setup -or -not $haveLlama -or -not (Test-Path $model)) {
    Write-Host '  downloading the model and llama.cpp (checksummed; about 5.5 GB the first time)'
    & pwsh -NoProfile -File (Join-Path $src 'scripts\setup.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'setup failed; see the messages above' }
} else {
    Write-Host '  model and llama.cpp: already in place'
}

# 2. Close the running copy the way the tray's Exit does, so it saves before the files are replaced.
$running = @(Get-Process GlobalAutocorrect -ErrorAction SilentlyContinue)
if ($running) {
    Write-Host "  closing the running copy"
    $exe = $running[0].Path
    if ($exe -and (Test-Path $exe)) { & $exe --exit | Out-Null }
    $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    # An older build without the exit signal, or one that hung: stop it.
    Get-Process GlobalAutocorrect -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

# From here on the user's copy is closed: whatever fails, start whichever copy is installed before giving up, so the
# user is never left without the app (a deploy once failed here and left it closed).
$exe = Join-Path $InstallDir 'GlobalAutocorrect.exe'
trap {
    Write-Host "  install failed: $_" -ForegroundColor Red
    if (-not $NoStart -and -not (Get-Process GlobalAutocorrect -ErrorAction SilentlyContinue) -and (Test-Path $exe)) {
        Write-Host '  starting the copy that is installed'
        Start-Process $exe -WorkingDirectory $InstallDir
    }
    break
}

# 3. Copy. robocopy /MIR makes the folder exactly the new build; exit codes below 8 mean success. A file the closing
# copy still holds for a moment is retried for up to 20 seconds.
New-Item -ItemType Directory -Force $InstallDir | Out-Null
robocopy (Join-Path $src 'app') $InstallDir /MIR /NFL /NDL /NJH /NJS /NP /R:20 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copying the app failed (robocopy exit $LASTEXITCODE)" }
New-Item -ItemType Directory -Force (Join-Path $InstallDir 'scripts') | Out-Null
Copy-Item (Join-Path $src 'scripts\*') (Join-Path $InstallDir 'scripts') -Force
foreach ($f in 'README.md', 'build.txt') { if (Test-Path (Join-Path $src $f)) { Copy-Item (Join-Path $src $f) $InstallDir -Force } }
$exe = Join-Path $InstallDir 'GlobalAutocorrect.exe'
Write-Host "  installed to $InstallDir"

# 4. Start it. Never elevated: from an admin prompt, hand the start to Explorer, which runs it as the normal user.
if (-not $NoStart) {
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($admin) { Start-Process explorer.exe -ArgumentList "`"$exe`"" } else { Start-Process $exe -WorkingDirectory $InstallDir }
    $ok = $false
    for ($i = 0; $i -lt 20 -and -not $ok; $i++) { Start-Sleep -Milliseconds 500; $ok = [bool](Get-Process GlobalAutocorrect -ErrorAction SilentlyContinue | Where-Object Path -eq $exe) }
    if (-not $ok) { throw 'Global Autocorrect did not start; see %APPDATA%\GlobalAutocorrect\crash.txt' }
    Write-Host '  running (look for the blue icon in the tray; Ctrl+Alt+P pauses it)'
}
Write-Host "Done: $build"
