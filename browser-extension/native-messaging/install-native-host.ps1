[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Chrome', 'Edge', 'Both')]
    [string]$Browser = 'Both',

    # App root that holds BrowserNativeHost.exe. Defaults to the portable layout
    # (this script ships in <AppRoot>\BrowserIntegration) and falls back to the
    # installed WinVaultWarden MSIX package.
    [string]$AppBaseDirectory
)

$ErrorActionPreference = 'Stop'

$hostName = 'com.winvaultwarden.browser'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrWhiteSpace($AppBaseDirectory)) {
    $portableRoot = Split-Path -Parent $scriptRoot
    if (Test-Path (Join-Path $portableRoot 'BrowserNativeHost.exe') -PathType Leaf) {
        $AppBaseDirectory = $portableRoot
    } else {
        $package = Get-AppxPackage -Name 'WinVaultWarden' |
            Sort-Object Version -Descending |
            Select-Object -First 1
        if (-not $package) {
            throw 'WinVaultWarden portable root or MSIX installation was not found. Pass -AppBaseDirectory.'
        }
        $AppBaseDirectory = $package.InstallLocation
    }
}

$appRoot = (Resolve-Path -LiteralPath $AppBaseDirectory).Path
$hostPath = Join-Path $appRoot 'BrowserNativeHost.exe'
if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) {
    throw "BrowserNativeHost.exe is missing from App root: $appRoot"
}

$targets = @{
    Chrome = @{
        Registry = 'HKCU\Software\Google\Chrome\NativeMessagingHosts'
        Template = 'com.winvaultwarden.browser.chrome.json.template'
        Directory = 'chrome'
    }
    Edge = @{
        Registry = 'HKCU\Software\Microsoft\Edge\NativeMessagingHosts'
        Template = 'com.winvaultwarden.browser.edge.json.template'
        Directory = 'edge'
    }
}
$selected = if ($Browser -eq 'Both') { @('Chrome', 'Edge') } else { @($Browser) }

foreach ($name in $selected) {
    $target = $targets[$name]

    $templatePath = Join-Path $scriptRoot $target.Template
    if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) {
        throw "Native host template is missing: $templatePath"
    }

    $template = Get-Content -LiteralPath $templatePath -Raw
    $manifestJson = $template.Replace(
        '%WINVAULTWARDEN_NATIVE_HOST_PATH%',
        ($hostPath.Replace('\', '\\')))
    if ($manifestJson -match '%[A-Z_]+%') {
        throw "Unresolved token in $($target.Template)."
    }

    $manifestRoot = Join-Path $env:LOCALAPPDATA "WinVaultWarden\BrowserNativeHost\$($target.Directory)"
    $manifestPath = Join-Path $manifestRoot "$hostName.json"
    $registryPath = "$($target.Registry)\$hostName"

    if ($PSCmdlet.ShouldProcess($manifestPath, "Register $name native host")) {
        New-Item -ItemType Directory -Path $manifestRoot -Force | Out-Null
        [IO.File]::WriteAllText($manifestPath, $manifestJson, [Text.UTF8Encoding]::new($false))
        & reg.exe add $registryPath /ve /t REG_SZ /d $manifestPath /f | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to register $name native host at $registryPath."
        }
        Write-Host "Registered $name native host -> $manifestPath"
    }
}

Write-Host "Host: $hostPath"
