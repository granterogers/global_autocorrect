#Requires -Version 7
<#
.SYNOPSIS
  Build, test and install the current code on this PC in one go: fast tests, package, then install.ps1 (which closes
  the running copy cleanly, replaces it and starts the new one). Claude Code runs this after every change the user
  asks for, so the installed app is always the latest build.
.EXAMPLE
  pwsh ./scripts/deploy.ps1
#>
param([switch] $SkipTests)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $SkipTests) {
    & pwsh -NoProfile -File (Join-Path $repo 'check.ps1') fast
    if ($LASTEXITCODE -ne 0) { throw 'fast tests failed: nothing was installed' }
}
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'package.ps1')
if ($LASTEXITCODE -ne 0) { throw 'packaging failed: nothing was installed' }
& pwsh -NoProfile -File (Join-Path $repo 'dist/GlobalAutocorrect/install.ps1')
if ($LASTEXITCODE -ne 0) { throw 'install failed' }
