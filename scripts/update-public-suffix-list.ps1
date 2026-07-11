[CmdletBinding()]
param(
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$Commit = 'b9a86cf0cd115f1e60b5815533f3fcfd2f9e8f4b',

    [ValidatePattern('^[0-9a-f]{64}$')]
    [string]$Sha256 = '71f10962519e15d087b1ccffffdbca3422838a4ff50a62a3852c23e896253e1f'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$targetRoot = Join-Path $repoRoot 'third_party\publicsuffix'
$baseUri = "https://raw.githubusercontent.com/publicsuffix/list/$Commit"
$client = [System.Net.Http.HttpClient]::new()

try {
    $data = $client.GetByteArrayAsync("$baseUri/public_suffix_list.dat").GetAwaiter().GetResult()
    $actual = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($data)).ToLowerInvariant()
    if ($actual -cne $Sha256) {
        throw "PSL SHA-256 mismatch. Expected $Sha256; received $actual."
    }

    $license = $client.GetStringAsync("$baseUri/LICENSE").GetAwaiter().GetResult()
    if (-not $license.StartsWith('Mozilla Public License Version 2.0')) {
        throw 'Unexpected PSL license text.'
    }

    New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $targetRoot 'public_suffix_list.dat'), $data)
    [IO.File]::WriteAllText(
        (Join-Path $targetRoot 'LICENSE'),
        $license,
        [Text.UTF8Encoding]::new($false))

    $provenance = [ordered]@{
        upstream = 'https://github.com/publicsuffix/list'
        commit = $Commit
        file = 'public_suffix_list.dat'
        sha256 = $Sha256
        license = 'MPL-2.0'
    } | ConvertTo-Json
    [IO.File]::WriteAllText(
        (Join-Path $targetRoot 'provenance.json'),
        $provenance + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}
finally {
    $client.Dispose()
}
