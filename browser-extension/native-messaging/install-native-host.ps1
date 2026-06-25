[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-p]{32}$')]
    [string]$ExtensionId,

    [ValidateSet('Chrome', 'Edge', 'Both')]
    [string]$Browser = 'Both',

    [string]$HostPath,

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipPublish,

    [switch]$SelfContained,

    [string]$ManifestOutputPath
)

$ErrorActionPreference = 'Stop'

$hostName = 'com.winvaultwarden.browser'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $scriptRoot '..\..')).Path
$platform = if ($RuntimeIdentifier -eq 'win-arm64') { 'arm64' } else { 'x64' }

if ([string]::IsNullOrWhiteSpace($HostPath)) {
    $publishDir = Join-Path $repoRoot "artifacts\native-host\$RuntimeIdentifier"

    if (-not $SkipPublish) {
        $projectPath = Join-Path $repoRoot 'src\BrowserNativeHost\BrowserNativeHost.csproj'
        $selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }

        & dotnet publish $projectPath `
            -c $Configuration `
            -r $RuntimeIdentifier `
            --self-contained:$selfContainedValue `
            -p:Platform=$platform `
            -o $publishDir

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE."
        }
    }

    $HostPath = Join-Path $publishDir 'BrowserNativeHost.exe'
}

if (-not (Test-Path -LiteralPath $HostPath -PathType Leaf)) {
    throw "Native host executable was not found: $HostPath"
}

$resolvedHostPath = (Resolve-Path -LiteralPath $HostPath).Path

if ([string]::IsNullOrWhiteSpace($ManifestOutputPath)) {
    $ManifestOutputPath = Join-Path $env:LOCALAPPDATA "WinVaultWarden\BrowserNativeHost\$hostName.json"
}

$manifestDirectory = Split-Path -Parent $ManifestOutputPath

$manifest = [ordered]@{
    name = $hostName
    description = 'WinVaultWarden browser integration native host'
    path = $resolvedHostPath
    type = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
}

$manifestJson = $manifest | ConvertTo-Json -Depth 4

if ($PSCmdlet.ShouldProcess($ManifestOutputPath, 'Write native host manifest')) {
    New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($ManifestOutputPath, $manifestJson, $utf8NoBom)
}

$registryTargets = switch ($Browser) {
    'Chrome' { @('HKCU\Software\Google\Chrome\NativeMessagingHosts') }
    'Edge' { @('HKCU\Software\Microsoft\Edge\NativeMessagingHosts') }
    'Both' {
        @(
            'HKCU\Software\Google\Chrome\NativeMessagingHosts',
            'HKCU\Software\Microsoft\Edge\NativeMessagingHosts'
        )
    }
}

foreach ($registryRoot in $registryTargets) {
    $registryPath = "$registryRoot\$hostName"

    if ($PSCmdlet.ShouldProcess($registryPath, "Register native host manifest $ManifestOutputPath")) {
        & reg.exe add $registryPath /ve /t REG_SZ /d $ManifestOutputPath /f | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to register native messaging host at $registryPath."
        }
    }
}

if ($WhatIfPreference) {
    Write-Host "Validated registration for $hostName"
} else {
    Write-Host "Registered $hostName"
}

Write-Host "Manifest: $ManifestOutputPath"
Write-Host "Host: $resolvedHostPath"
