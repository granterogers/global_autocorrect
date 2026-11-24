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
$env:WRANGLER_SEND_METRICS = 'false'   # Wrangler's usage reports to Cloudflare: off
$wrangler = 'wrangler@4.158.0'
$repo = Split-Path $PSScriptRoot -Parent
$toml = Join-Path $PSScriptRoot 'wrangler.toml'
# Not named Npx: PowerShell names ignore case, so it would call itself instead of npx.
# npx.cmd, not npx: that is npm's PowerShell wrapper (npx.ps1), which loses the arguments when called from a function.
# --config on every call: run through npx, Wrangler doesn't find relay/wrangler.toml by itself, and a deploy without it
# looks for a static site instead ("Could not detect a directory containing static files").
function Invoke-Wrangler { & npx.cmd --yes $wrangler @args --config $toml }
# A secret goes in on standard input, never on the command line (and a function doesn't pass its pipeline on by itself).
function Set-RelaySecret([string]$Name, [string]$Value) { $Value | & npx.cmd --yes $wrangler secret put $Name --config $toml | Out-Null }
# The storage entry itself, not the word in the file's opening comment.
function Test-Storage { (Get-Content $toml -Raw) -match '(?m)^\[\[kv_namespaces\]\]' }

Push-Location $PSScriptRoot
try {
    # 1. Cloudflare sign-in.
    $who = (Invoke-Wrangler whoami 2>&1 | Out-String)
    if ($who -notmatch 'associated with the email|Account ID') {
        Write-Host 'Opening Cloudflare in your browser: sign in (or make a free account), then allow Wrangler.'
        Invoke-Wrangler login
        if ($LASTEXITCODE -ne 0) { throw 'Cloudflare sign-in did not finish.' }
    }

    # 2. Storage.
    # Wrangler refuses a config whose storage entry has no id, so the entry is only added once the storage exists.
    # --update-config: Wrangler writes the entry in itself rather than asking (a question nobody would see, as its
    # output is captured here); if it didn't, the entry is added from the id it printed.
    if (-not (Test-Storage)) {
        $out = (Invoke-Wrangler kv namespace create LEARNING --binding LEARNING --update-config 2>&1 | Out-String)
        if ($out -notmatch '([0-9a-f]{32})') { throw "Couldn't make the relay's storage:`n$out" }
        $id = $Matches[1]
        if (-not (Test-Storage)) {
            $text = (Get-Content $toml -Raw).TrimEnd() + "`n`n[[kv_namespaces]]`nbinding = `"LEARNING`"`nid = `"$id`"`n"
            Set-Content $toml $text -NoNewline
        }
        Write-Host "  storage: $id"
    }

    # 3. Deploy. Shown as it happens the first time: a new account is asked to pick its workers.dev name. Then once more,
    # quietly (nothing to ask by then), to read the relay's address from what it prints.
    Invoke-Wrangler deploy
    if ($LASTEXITCODE -ne 0) { throw 'Deploy failed; see the messages above.' }
    $out = (Invoke-Wrangler deploy 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $out -notmatch '(https://[a-z0-9.-]+\.workers\.dev)') { throw "Couldn't read the relay's address:`n$out" }
    $url = "$($Matches[1])/v1/learning"
    Write-Host "  relay: $url"

    # 4. Secrets, only when the relay doesn't have them yet (or asked).
    $have = (Invoke-Wrangler secret list 2>&1 | Out-String)
    if ($NewTokens -or $have -notmatch 'READ_TOKEN') {
        $token = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
        Set-RelaySecret READ_TOKEN $token
        if ($LASTEXITCODE -ne 0) { throw 'Setting the relay read token failed.' }
        $token | gh secret set COMMUNITY_READ_TOKEN --repo granterogers/global_autocorrect
        if ($LASTEXITCODE -ne 0) { throw 'Setting the GitHub secret failed (is gh signed in?).' }
        $token = $null
        Write-Host '  read token: set on the relay and in GitHub'
    }
    if ($NewTokens -or $have -notmatch 'SALT') {
        Set-RelaySecret SALT ([Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
        if ($LASTEXITCODE -ne 0) { throw 'Setting the relay salt failed.' }
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
