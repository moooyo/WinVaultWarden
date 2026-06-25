[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Chrome', 'Edge', 'Both')]
    [string]$Browser = 'Both',

    [switch]$RemoveManifest,

    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'

$hostName = 'com.winvaultwarden.browser'

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

    if ($PSCmdlet.ShouldProcess($registryPath, 'Unregister native host')) {
        & reg.exe delete $registryPath /f 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1) {
            throw "Failed to unregister native messaging host at $registryPath."
        }
    }
}

if ($RemoveManifest) {
    if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
        $ManifestPath = Join-Path $env:LOCALAPPDATA "WinVaultWarden\BrowserNativeHost\$hostName.json"
    }

    if ((Test-Path -LiteralPath $ManifestPath -PathType Leaf) `
        -and $PSCmdlet.ShouldProcess($ManifestPath, 'Remove native host manifest')) {
        Remove-Item -LiteralPath $ManifestPath -Force
    }
}

Write-Host "Unregistered $hostName"
