#Requires -Version 7
<#
.SYNOPSIS
  Builds the release-candidate zip: the app, the setup and verify scripts, the README and the latest metrics (13.1).
  Models and llama.cpp are not in the zip; setup downloads them with checksums (5.7, 11.4).
#>
param([string] $Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $repo 'src/GlobalAutocorrect.App/GlobalAutocorrect.App.csproj'
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$stage = Join-Path $Out 'package/GlobalAutocorrect'
Remove-Item (Join-Path $Out 'package') -Recurse -Force -ErrorAction SilentlyContinue

# Framework-dependent: this is for the user's own PC, which has .NET 10 (3.2).
dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o (Join-Path $stage 'app') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

New-Item -ItemType Directory -Force (Join-Path $stage 'scripts') | Out-Null
Copy-Item (Join-Path $repo 'scripts/setup.ps1'), (Join-Path $repo 'scripts/verify.ps1'), (Join-Path $repo 'scripts/manifest.json') (Join-Path $stage 'scripts')
Copy-Item (Join-Path $repo 'README.md') $stage
Copy-Item (Join-Path $repo 'assets/NOTICE.md') $stage
foreach ($f in 'metrics.json', 'eval-report.md') {
    $p = Join-Path $Out $f
    if (Test-Path $p) { Copy-Item $p $stage }
}

$zip = Join-Path $Out "GlobalAutocorrect-$version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "package: $zip ($size MB)"
