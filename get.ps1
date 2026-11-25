<#
.SYNOPSIS
  D178: installs Global Autocorrect with one command, on a PC that has never seen it.

.DESCRIPTION
  Paste this into the Start menu's "Windows PowerShell" (or any PowerShell):

    powershell -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/granterogers/global_autocorrect/main/get.ps1 | iex"

  It runs in the PowerShell that comes with Windows (5.1) as well as PowerShell 7, and:
    1. Checks the PC can run the app (Windows 11, an NVIDIA card or the slower CPU fallback, room for the model).
    2. Installs PowerShell 7 with winget if it isn't there (the app's install and setup scripts need it). Without winget
       (or if winget fails) it unpacks PowerShell's official zip for this user only, checked against the hashes
       published with it (D182).
    3. Downloads the latest release's zip and checks it against the release's SHA256SUMS.txt (D58).
    4. Unzips it to a new temporary folder and runs its install.ps1, which downloads the model and llama.cpp with
       their own checksums, installs to %LOCALAPPDATA%\Programs\GlobalAutocorrect and starts the app.
  After that the app keeps itself up to date (D174) and downloads the brain (D102).

  It only reads from GitHub and winget. It sends nothing about the PC or the person (8.6).
  When it stops without installing, it says why and, run as above, exits with code 1 (pasted as `irm ... | iex` into a
  window of your own, it leaves that window open instead).
  $env:GA_GET_DRYRUN = '1' stops after the download and checksum, before anything is installed (for testing).
  $env:GA_GET_ZIP = '<path to a package zip>' installs that zip, checked against the SHA256SUMS.txt beside it, instead of
  the latest release (for testing a build before it is released: scripts/sandbox-install-test.ps1 -Package).
#>
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Windows PowerShell 5.1's progress bar makes downloads many times slower
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $repo = 'granterogers/global_autocorrect'

    function Say($m) { Write-Host "  $m" }
    function Stop-Install($m) { Write-Host ''; Write-Host "  Not installed: $m" -ForegroundColor Red; throw 'stopped' }

    function Test-Requirements {
        # What this PC has. Everything the decision below needs, gathered in one place.
        $os = Get-CimInstance Win32_OperatingSystem
        $info = [pscustomobject]@{
            Windows11 = [int]$os.BuildNumber -ge 22000
            Nvidia    = [bool](Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'NVIDIA' })
            FreeGB    = [math]::Round((Get-PSDrive ($env:LOCALAPPDATA.Substring(0, 1))).Free / 1GB, 1)
            Is64Bit   = [Environment]::Is64BitOperatingSystem
        }

        Say "Windows 11: $($info.Windows11)  NVIDIA card: $($info.Nvidia)  free space: $($info.FreeGB) GB"
        # Stop only for what can't work; warn for what works but is untested or slower (D178).
        if (-not $info.Is64Bit) { Stop-Install 'the app and llama.cpp need 64-bit Windows.' }
        # About 5.5 GB of model and llama.cpp, the app, the unzipped package and room for the next update.
        if ($info.FreeGB -lt 8) { Stop-Install "it needs about 8 GB free on drive $($env:LOCALAPPDATA.Substring(0, 2)) and there are $($info.FreeGB) GB." }
        if (-not $info.Windows11) { Say 'warning: built and tested on Windows 11 only; carrying on.' }
        if (-not $info.Nvidia) { Say 'warning: no NVIDIA card found, so the AI runs on the CPU with a smaller model: clean-ups take seconds, not a fraction of one.' }
    }

    $headers = @{ 'User-Agent' = 'GlobalAutocorrect-get' }
    # The nightly check runs on shared GitHub machines, whose unsigned-in API allowance others use up.
    if ($env:GA_GET_TOKEN) { $headers['Authorization'] = "Bearer $env:GA_GET_TOKEN" }

    function Get-Text($url) {
        $t = (Invoke-WebRequest $url -UseBasicParsing).Content
        if ($t -is [byte[]]) {
            # By its byte-order mark: PowerShell's own hashes.sha256 is UTF-16 (FF FE); the release's list is plain ASCII.
            $t = if ($t.Length -ge 2 -and $t[0] -eq 0xFF -and $t[1] -eq 0xFE) { [Text.Encoding]::Unicode.GetString($t, 2, $t.Length - 2) }
                 elseif ($t.Length -ge 3 -and $t[0] -eq 0xEF -and $t[1] -eq 0xBB -and $t[2] -eq 0xBF) { [Text.Encoding]::UTF8.GetString($t, 3, $t.Length - 3) }
                 else { [Text.Encoding]::UTF8.GetString($t) }
        }
        return $t
    }

    # D58: a download is used only if a published checksum list names it and its SHA-256 matches.
    function Assert-Checksum($file, $name, $sums, $what) {
        $line = ($sums -split "`n") | Where-Object { $_ -match "^\s*([0-9a-fA-F]{64})\s+\*?$([regex]::Escape($name))\s*$" } | Select-Object -First 1
        if (-not $line) { Stop-Install "$name is not in $what's checksum list." }
        $want = ($line.Trim() -split '\s+')[0].ToLowerInvariant()
        $got = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($got -ne $want) { Stop-Install "the $what download doesn't match its published checksum (got $got). Try again." }
    }

    # D182: where this script puts PowerShell 7 when winget can't; the app's own updater looks here too.
    $ownPwsh = Join-Path $env:LOCALAPPDATA 'GlobalAutocorrect\pwsh\pwsh.exe'

    function Install-OwnPwsh {
        # PowerShell 7.7 ships zips, no installer: unpack the official x64 one for this user (no admin rights needed).
        Say 'installing PowerShell 7 for this user (its official zip)'
        $rel = Invoke-RestMethod 'https://api.github.com/repos/PowerShell/PowerShell/releases/latest' -Headers @{ 'User-Agent' = 'GlobalAutocorrect-get' }
        $zipA = $rel.assets | Where-Object { $_.name -match '^PowerShell-[\d.]+-win-x64\.zip$' } | Select-Object -First 1
        $sumA = $rel.assets | Where-Object { $_.name -eq 'hashes.sha256' } | Select-Object -First 1
        if (-not $zipA -or -not $sumA) { return $false }
        $dl = Join-Path ([IO.Path]::GetTempPath()) ('GlobalAutocorrect-pwsh-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Path $dl | Out-Null
        try {
            $zip = Join-Path $dl $zipA.name
            Invoke-WebRequest $zipA.browser_download_url -OutFile $zip -UseBasicParsing
            Assert-Checksum $zip $zipA.name (Get-Text $sumA.browser_download_url) "PowerShell $($rel.tag_name)"
            $target = Split-Path $ownPwsh
            Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
            Expand-Archive $zip -DestinationPath $target
            Get-ChildItem $target -Recurse -File | Unblock-File
        }
        finally { Remove-Item $dl -Recurse -Force -ErrorAction SilentlyContinue }
        return (Test-Path $ownPwsh)
    }

    function Get-Pwsh {
        $found = Get-Command pwsh -ErrorAction SilentlyContinue
        if ($found) { return $found.Source }
        $default = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
        if (Test-Path $default) { return $default }
        if (Test-Path $ownPwsh) { return $ownPwsh }
        if (Get-Command winget -ErrorAction SilentlyContinue) {
            Say 'installing PowerShell 7 (winget)'
            winget install --id Microsoft.PowerShell --exact --source winget --silent --accept-package-agreements --accept-source-agreements | Out-Null
            if (Test-Path $default) { return $default }   # a new install isn't on this window's PATH yet
            Say 'winget did not install it; trying the official zip instead'
        }
        if (Install-OwnPwsh) { return $ownPwsh }
        Stop-Install 'PowerShell 7 could not be installed. Install it from https://aka.ms/powershell, then run this again.'
    }

    try {
        Write-Host ''
        Write-Host '  Global Autocorrect: typo-free typing in every Windows app, from a private AI on your own GPU.'
        Write-Host ''
        Test-Requirements
        $pwsh = Get-Pwsh
        Say "PowerShell 7: $pwsh"

        # A new, empty folder of its own for what is downloaded (nothing else is run from it but the release's install.ps1).
        $work = Join-Path ([IO.Path]::GetTempPath()) ('GlobalAutocorrect-get-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Path $work | Out-Null
        if ($env:GA_GET_ZIP) {
            # D182, testing only (scripts/sandbox-install-test.ps1 -Package): a package built on the developer's PC, with
            # a SHA256SUMS.txt beside it, in place of the latest release. Checked the same way.
            $name = Split-Path $env:GA_GET_ZIP -Leaf
            $zip = Join-Path $work $name
            Say "test package: $name"
            Copy-Item $env:GA_GET_ZIP $zip
            Assert-Checksum $zip $name (Get-Content (Join-Path (Split-Path $env:GA_GET_ZIP) 'SHA256SUMS.txt') -Raw) 'the test package'
        }
        else {
            $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers $headers
            $zipAsset = $release.assets | Where-Object { $_.name -match '^GlobalAutocorrect-[\d.]+\.zip$' } | Select-Object -First 1
            $sumAsset = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' } | Select-Object -First 1
            if (-not $zipAsset -or -not $sumAsset) { Stop-Install "the latest release ($($release.tag_name)) has no zip or checksum list." }
            Say "latest version: $($release.tag_name)"
            $zip = Join-Path $work $zipAsset.name
            Say "downloading $($zipAsset.name) ($([math]::Round($zipAsset.size / 1MB)) MB)"
            Invoke-WebRequest $zipAsset.browser_download_url -OutFile $zip -UseBasicParsing
            # D58: the same check the app's own update makes. A zip that isn't listed, or doesn't match, is never opened.
            Assert-Checksum $zip $zipAsset.name (Get-Text $sumAsset.browser_download_url) 'the release'
        }
        Say 'checksum: matches the release'

        if ($env:GA_GET_DRYRUN -eq '1') { Say "dry run: stopped before installing ($zip)"; return }

        $unzipped = Join-Path $work 'GlobalAutocorrect'
        Expand-Archive $zip -DestinationPath $unzipped
        $install = Get-ChildItem $unzipped -Filter install.ps1 -Recurse -Depth 1 | Select-Object -First 1
        if (-not $install) { Stop-Install 'the release zip has no install.ps1.' }
        Get-ChildItem $unzipped -Recurse -File | Unblock-File

        Write-Host ''
        & $pwsh -NoProfile -ExecutionPolicy Bypass -File $install.FullName
        if ($LASTEXITCODE -ne 0) { Stop-Install 'install.ps1 reported a problem; see the messages above.' }
        Write-Host ''
        Write-Host '  Done. Global Autocorrect is running in the system tray and keeps itself up to date.' -ForegroundColor Green
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
    catch {
        if ($_.Exception.Message -ne 'stopped') { Write-Host "  Not installed: $($_.Exception.Message)" -ForegroundColor Red }
        # D182: a failure a script can see. Only in the window the README's command opens for this (its command line names
        # this file); pasted as irm ... | iex into the user's own window, exiting would close that window.
        if ([Environment]::CommandLine -match 'get\.ps1') { exit 1 }
    }
}
