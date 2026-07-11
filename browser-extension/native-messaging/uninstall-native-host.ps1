[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Chrome', 'Edge', 'Both')]
    [string]$Browser = 'Both'
)

# Portable-only: this removes the per-user (HKCU) native-messaging registration and manifest
# that install-native-host.ps1 writes for the portable build. The MSIX package never registers
# a Host, so there is nothing MSIX-side to clean up. Missing entries are treated as success.
$ErrorActionPreference = 'Stop'

$hostName = 'com.winvaultwarden.browser'

$targets = @{
    Chrome = @{
        Registry = 'HKCU\Software\Google\Chrome\NativeMessagingHosts'
        Directory = 'chrome'
    }
    Edge = @{
        Registry = 'HKCU\Software\Microsoft\Edge\NativeMessagingHosts'
        Directory = 'edge'
    }
}
$selected = if ($Browser -eq 'Both') { @('Chrome', 'Edge') } else { @($Browser) }

foreach ($name in $selected) {
    $target = $targets[$name]
    $registryPath = "$($target.Registry)\$hostName"

    if ($PSCmdlet.ShouldProcess($registryPath, "Unregister $name native host")) {
        # /f makes missing keys idempotent: reg.exe returns 1 (not found) which we treat as success.
        & reg.exe delete $registryPath /f 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1) {
            throw "Failed to unregister $name native messaging host at $registryPath."
        }
    }

    $manifestPath = Join-Path $env:LOCALAPPDATA "WinVaultWarden\BrowserNativeHost\$($target.Directory)\$hostName.json"
    if ((Test-Path -LiteralPath $manifestPath -PathType Leaf) `
        -and $PSCmdlet.ShouldProcess($manifestPath, "Remove $name native host manifest")) {
        Remove-Item -LiteralPath $manifestPath -Force
    }
}

Write-Host "Unregistered $hostName ($Browser)"
