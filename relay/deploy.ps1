#requires -Version 7
<#
.SYNOPSIS
  D179: sets up the shared-learning relay on Cloudflare (free plan) and points every install at it. Run once; safe to run
  again (it redeploys the relay and keeps its storage and secrets unless -NewTokens).

.DESCRIPTION
  1. Signs in to Cloudflare in your browser if needed (makes an account there the first time; you do that part).
  2. Makes the relay's storage (a KV namespace) and writes its id into relay/wrangler.toml.
  3. Deploys relay/worker.mjs and reads its address.
  4. Makes two random secrets and hands them straight to where they're used, never printed or saved anywhere else:
     READ_TOKEN (the relay, and the GitHub secret COMMUNITY_READ_TOKEN the collecting job uses) and SALT (the relay).
  5. Writes the address into brain/brain.json, then commits and pushes it (a brain-only push: no version). Every
     running copy picks it up within 15 minutes; the About page's "Share what it learns" switch then works.

  Needs Node.js (npx), git and gh signed in to GitHub with access to this repository.
#>
param(
    [switch]$NewTokens,     # make and set new secrets even if the relay already has them
    [switch]$NoPush         # write the brain but leave committing and pushing to you
)
$ErrorActionPreference = 'Stop'
$wrangler = 'wrangler@4.158.0'
$repo = Split-Path $PSScriptRoot -Parent
$toml = Join-Path $PSScriptRoot 'wrangler.toml'
function Npx { & npx --yes $wrangler @args }

Push-Location $PSScriptRoot
try {
    # 1. Cloudflare sign-in.
    $who = (Npx whoami 2>&1 | Out-String)
    if ($who -match 'not authenticated|You are not logged in') {
        Write-Host 'Opening Cloudflare in your browser: sign in (or make a free account), then allow Wrangler.'
        Npx login
        if ($LASTEXITCODE -ne 0) { throw 'Cloudflare sign-in did not finish.' }
    }

    # 2. Storage.
    $text = Get-Content $toml -Raw
    if ($text -match 'id = ""') {
        $out = (Npx kv namespace create LEARNING 2>&1 | Out-String)
        if ($out -notmatch '([0-9a-f]{32})') { throw "Couldn't make the relay's storage:`n$out" }
        $text = $text -replace 'id = ""', "id = `"$($Matches[1])`""
        Set-Content $toml $text -NoNewline
        Write-Host "  storage: $($Matches[1])"
    }

    # 3. Deploy.
    $out = (Npx deploy 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $out -notmatch '(https://[a-z0-9.-]+\.workers\.dev)') { throw "Deploy failed:`n$out" }
    $url = "$($Matches[1])/v1/learning"
    Write-Host "  relay: $url"

    # 4. Secrets, only when the relay doesn't have them yet (or asked).
    $have = (Npx secret list 2>&1 | Out-String)
    if ($NewTokens -or $have -notmatch 'READ_TOKEN') {
        $token = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
        $token | Npx secret put READ_TOKEN | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Setting the relay read token failed.' }
        $token | gh secret set COMMUNITY_READ_TOKEN --repo granterogers/global_autocorrect
        if ($LASTEXITCODE -ne 0) { throw 'Setting the GitHub secret failed (is gh signed in?).' }
        $token = $null
        Write-Host '  read token: set on the relay and in GitHub'
    }
    if ($NewTokens -or $have -notmatch 'SALT') {
        [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) | Npx secret put SALT | Out-Null
        Write-Host '  salt: set on the relay'
    }
}
finally { Pop-Location }

# 5. Point every install at it.
Push-Location $repo
try {
    dotnet run --project tools/GlobalAutocorrect.DataGen -c Release -- brain-share --url $url --out brain/brain.json
    if ($LASTEXITCODE -ne 0) { throw 'Writing the address into the brain failed.' }
    if ($NoPush) { Write-Host 'Brain written; commit and push brain/brain.json and relay/wrangler.toml when ready.'; return }
    git add brain/brain.json relay/wrangler.toml
    git diff --cached --quiet
    if ($LASTEXITCODE -eq 0) { Write-Host 'Already pointing at this relay: nothing to push.'; return }
    git commit -q -m "Brain (D179): installs that share what they learn send it to the relay"
    git push
    Write-Host 'Done. Every running copy picks up the relay within 15 minutes.'
}
finally { Pop-Location }
