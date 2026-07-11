[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Chrome', 'Edge', 'Both')]
    [string]$Browser = 'Both',

    # App root that holds BrowserNativeHost.exe. Defaults to the portable layout
    # (this script ships in <PortableRoot>\BrowserIntegration).
    #
    # PORTABLE-ONLY: browser integration is supported for the portable build only.
    # The MSIX package deliberately does not carry, register, or claim the Host, so an
    # MSIX install location (Program Files\WindowsApps, or a folder with an
    # AppxManifest.xml) is rejected loudly instead of writing any registry/manifest.
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
        throw "未找到便携版 WinVaultWarden 根目录中的 BrowserNativeHost.exe (查找位置: $portableRoot)。请在便携版布局下运行,或用 -AppBaseDirectory 指定便携版根目录。"
    }
}

$appRoot = (Resolve-Path -LiteralPath $AppBaseDirectory).Path

# Reject MSIX-packaged locations: browser integration is portable-only. WindowsApps is the
# canonical MSIX install root; an AppxManifest.xml next to the Host is the same signal for
# a sideloaded/staged package. Either way we must never register from a packaged location.
if ($appRoot -match '(?i)\\Program Files\\WindowsApps\\' -or
    (Test-Path -LiteralPath (Join-Path $appRoot 'AppxManifest.xml') -PathType Leaf)) {
    throw "MSIX 不支持浏览器集成，请使用便携版 (portable) 安装。检测到的 MSIX 包目录: $appRoot"
}

$hostPath = Join-Path $appRoot 'BrowserNativeHost.exe'
if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) {
    throw "BrowserNativeHost.exe is missing from portable App root: $appRoot"
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
