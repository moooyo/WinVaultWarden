[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [switch]$SkipMsix
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'src\App\App.csproj'
$arch = if ($RuntimeIdentifier -eq 'win-arm64') { 'arm64' } else { 'x64' }
$verifyRoot = Join-Path $repoRoot "artifacts\browser-package-verification\$RuntimeIdentifier"
$hostName = 'BrowserNativeHost.exe'

New-Item $verifyRoot -ItemType Directory -Force | Out-Null

function Resolve-MSBuild {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw "vswhere.exe not found at $vswhere"
    }
    $msbuild = & $vswhere -latest -prerelease -products * `
        -requires Microsoft.Component.MSBuild `
        -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($msbuild) -or -not (Test-Path -LiteralPath $msbuild)) {
        throw 'VS MSBuild.exe was not found via vswhere.'
    }
    return $msbuild
}

# Chromium id recomputed from the shipped public key; the native manifest origin must match it.
function Get-ChromiumId([string]$publicKey) {
    $hash = [Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($publicKey))
    $alphabet = 'abcdefghijklmnop'
    -join ($hash[0..15] | ForEach-Object {
        $alphabet[$_ -shr 4]
        $alphabet[$_ -band 0x0f]
    })
}

$msbuild = Resolve-MSBuild
Write-Host "MSBuild: $msbuild"

# --- 1. Publish the unpackaged (portable) self-contained AOT build. ---
$portable = Join-Path $verifyRoot 'portable'
Remove-Item $portable -Recurse -Force -ErrorAction SilentlyContinue
New-Item $portable -ItemType Directory -Force | Out-Null

& $msbuild $project -restore -t:Publish `
    -p:Configuration=$Configuration `
    -p:Platform=$arch `
    -p:RuntimeIdentifier=$RuntimeIdentifier `
    -p:WindowsPackageType=None `
    -p:SelfContained=true `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishDir="$portable\" `
    -nologo -m
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed ($LASTEXITCODE)." }

# --- 2. Portable root must carry exactly one Host, at the locator path. ---
$portableHosts = @(Get-ChildItem -LiteralPath $portable -Filter $hostName -File)
if ($portableHosts.Count -ne 1) {
    throw "Expected exactly one $hostName in portable root '$portable', found $($portableHosts.Count)."
}
$expectedPortableHost = [IO.Path]::GetFullPath((Join-Path $portable $hostName))
if ($portableHosts[0].FullName -ne $expectedPortableHost) {
    throw "Portable Host path '$($portableHosts[0].FullName)' does not match locator path '$expectedPortableHost'."
}
Write-Host "Portable Host OK: $($portableHosts[0].FullName)"

$appExe = Join-Path $portable 'App.exe'
if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) {
    throw "Portable root is missing App.exe next to the Host: $portable"
}

# --- 3+4. Build an unsigned MSIX, unpack it, and assert the Host is NOT packaged. ---
# User decision: browser integration is portable-only. The MSIX package must not carry,
# register, or claim the Host, so the package root has no BrowserNativeHost.exe and the
# manifest declares neither a fullTrustProcess nor an appExecutionAlias extension.
if (-not $SkipMsix) {
    $msixOut = Join-Path $verifyRoot 'msix'
    Remove-Item $msixOut -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $msixOut -ItemType Directory -Force | Out-Null

    & $msbuild $project -restore -t:Build `
        -p:Configuration=$Configuration `
        -p:Platform=$arch `
        -p:GenerateAppxPackageOnBuild=true `
        -p:AppxPackageSigningEnabled=false `
        -p:UapAppxPackageBuildMode=SideloadOnly `
        -p:AppxBundle=Never `
        -p:AppxPackageDir="$msixOut\" `
        -nologo -m
    if ($LASTEXITCODE -ne 0) { throw "MSIX build failed ($LASTEXITCODE)." }

    $msix = Get-ChildItem $msixOut -Recurse -Filter *.msix |
        Where-Object { $_.Name -notmatch '\.appxsym$' } | Select-Object -First 1
    if (-not $msix) { throw "No .msix produced under $msixOut" }

    $kitsBin = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    $makeappx = Get-ChildItem $kitsBin -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -match '\\x64\\' |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $makeappx) { throw "makeappx.exe not found under $kitsBin" }

    $unpacked = Join-Path $verifyRoot 'msix-unpacked'
    Remove-Item $unpacked -Recurse -Force -ErrorAction SilentlyContinue
    & $makeappx.FullName unpack /p $msix.FullName /d $unpacked /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makeappx unpack failed ($LASTEXITCODE)." }

    $packagedHost = Join-Path $unpacked $hostName
    if (Test-Path -LiteralPath $packagedHost -PathType Leaf) {
        throw "MSIX package must NOT carry the Host, but $hostName is present at $unpacked."
    }
    $strayHosts = @(Get-ChildItem -LiteralPath $unpacked -Recurse -Filter $hostName -File -ErrorAction SilentlyContinue)
    if ($strayHosts.Count -ne 0) {
        throw "MSIX package must NOT carry the Host, but found: $($strayHosts.FullName -join ', ')."
    }
    Write-Host "MSIX has no Host (portable-only) OK."

    $manifestPath = Join-Path $unpacked 'AppxManifest.xml'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Unpacked MSIX is missing AppxManifest.xml at $unpacked"
    }
    $manifestText = Get-Content -LiteralPath $manifestPath -Raw
    $xml = [xml]$manifestText
    foreach ($category in @('windows.fullTrustProcess', 'windows.appExecutionAlias')) {
        $claimed = $xml.GetElementsByTagName('*') | Where-Object {
            $_.GetAttribute('Category') -eq $category
        }
        if ($claimed) {
            throw "AppxManifest.xml must not declare '$category' for the portable-only Host."
        }
    }
    if ($manifestText -match '(?i)BrowserNativeHost') {
        throw "AppxManifest.xml must not reference BrowserNativeHost (portable-only)."
    }
    Write-Host "MSIX manifest claims no Host (no fullTrustProcess/appExecutionAlias) OK."
}

# --- 5. Resolve the shipped templates and assert token/origin correctness. ---
$identities = Get-Content (Join-Path $repoRoot 'browser-extension\extension-identities.json') -Raw | ConvertFrom-Json
$generated = Join-Path $verifyRoot 'native-manifests'
Remove-Item $generated -Recurse -Force -ErrorAction SilentlyContinue
New-Item $generated -ItemType Directory -Force | Out-Null

foreach ($browser in @('chrome', 'edge')) {
    $identity = $identities.$browser
    if ((Get-ChromiumId $identity.publicKey) -cne $identity.id) {
        throw "$browser public key does not recompute to its fixed id."
    }

    $templatePath = Join-Path $portable "BrowserIntegration\com.winvaultwarden.browser.$browser.json.template"
    if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) {
        throw "Portable payload is missing the $browser native manifest template: $templatePath"
    }

    $token = '%WINVAULTWARDEN_NATIVE_HOST_PATH%'
    $rendered = (Get-Content -LiteralPath $templatePath -Raw).Replace(
        $token, ($portableHosts[0].FullName.Replace('\', '\\')))
    if ($rendered -match '%[A-Z_]+%') {
        throw "$browser native manifest still contains an unresolved token."
    }

    $renderedPath = Join-Path $generated "$browser.json"
    [IO.File]::WriteAllText($renderedPath, $rendered, [Text.UTF8Encoding]::new($false))
    $manifest = Get-Content -LiteralPath $renderedPath -Raw | ConvertFrom-Json

    $origin = @($manifest.allowed_origins)
    if ($origin.Count -ne 1 -or $origin[0] -cne "chrome-extension://$($identity.id)/") {
        throw "$browser native manifest origin '$($origin -join ',')' does not match id $($identity.id)."
    }
    if ($manifest.path -ne $portableHosts[0].FullName) {
        throw "$browser native manifest path '$($manifest.path)' does not point at the shipped Host."
    }
    Write-Host "$browser native manifest OK: $($origin[0])"
}

Write-Host ""
Write-Host "Package verification PASSED for $RuntimeIdentifier ($Configuration)."
