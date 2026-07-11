[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\browser-extension')
)

$ErrorActionPreference = 'Stop'

$identities = Get-Content (Join-Path $PSScriptRoot 'extension-identities.json') -Raw | ConvertFrom-Json

function Get-ChromiumId([string]$publicKey) {
    $hash = [Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($publicKey))
    $alphabet = 'abcdefghijklmnop'
    -join ($hash[0..15] | ForEach-Object {
        $alphabet[$_ -shr 4]
        $alphabet[$_ -band 0x0f]
    })
}

New-Item $OutputDirectory -ItemType Directory -Force | Out-Null

foreach ($browser in @('chrome', 'edge')) {
    $identity = $identities.$browser

    # The extension id is derived from the public key; never trust a hand-written constant.
    if ((Get-ChromiumId $identity.publicKey) -cne $identity.id) {
        throw "$browser public key does not produce fixed extension id."
    }

    $template = Get-Content (Join-Path $PSScriptRoot "native-messaging\com.winvaultwarden.browser.$browser.json.template") -Raw
    if (-not $template.Contains("chrome-extension://$($identity.id)/")) {
        throw "$browser native manifest origin does not match extension id."
    }

    $manifestFile = if ($browser -eq 'chrome') { 'manifest.json' } else { 'manifest.edge.json' }
    $manifest = Get-Content (Join-Path $PSScriptRoot $manifestFile) -Raw | ConvertFrom-Json
    if ((Get-ChromiumId $manifest.key) -cne $identity.id) {
        throw "$browser $manifestFile key does not produce fixed extension id."
    }

    $stage = Join-Path $OutputDirectory $browser
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $stage -ItemType Directory -Force | Out-Null

    # Ship only the runtime bridge: src/ scripts plus the browser-specific manifest as manifest.json.
    # No manifest.edge.json, no test-pages, no install scripts, and no private keys.
    Copy-Item (Join-Path $PSScriptRoot 'src') $stage -Recurse
    Copy-Item (Join-Path $PSScriptRoot $manifestFile) (Join-Path $stage 'manifest.json')

    $leaked = Get-ChildItem $stage -Recurse -File |
        Where-Object { $_.Name -in @('manifest.edge.json', 'extension-identities.json') -or $_.Extension -in @('.ps1', '.pem', '.pfx', '.key') }
    if ($leaked) {
        throw "$browser package staged forbidden files: $($leaked.FullName -join ', ')"
    }

    $zip = Join-Path $OutputDirectory "winvaultwarden-$browser.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
    Write-Host "Packed $browser extension ($($identity.id)) -> $zip"
}
