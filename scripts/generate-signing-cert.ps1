#requires -Version 5.1
<#
.SYNOPSIS
  Generate a self-signed code-signing certificate for WinVaultWarden MSIX signing.
.DESCRIPTION
  Creates a CodeSigningCert whose Subject (CN=WinVaultWarden) MUST match
  Package.appxmanifest's Publisher. Exports into .secrets/ (gitignored):
    - WinVaultWarden-Signing.pfx         private key  -> KEEP SECRET
    - WinVaultWarden-Signing.cer         public cert  -> ship to users to trust
    - WinVaultWarden-Signing.pfx.base64  -> GitHub Secret SIGNING_PFX_BASE64
  Then set repo secrets:
    SIGNING_PFX_BASE64   = contents of the .pfx.base64 file
    SIGNING_PFX_PASSWORD = the -Password you passed here
.EXAMPLE
  pwsh ./scripts/generate-signing-cert.ps1 -Password 'Some-Strong-Pass'
#>
param(
  [Parameter(Mandatory = $true)][string]$Password,
  [string]$Subject = 'CN=WinVaultWarden',
  [string]$OutDir,
  [int]$ValidYears = 5
)
$ErrorActionPreference = 'Stop'

if (-not $OutDir) {
  $root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
  $OutDir = Join-Path $root '..\.secrets'
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$cert = New-SelfSignedCertificate `
  -Type CodeSigningCert `
  -Subject $Subject `
  -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
  -CertStoreLocation 'Cert:\CurrentUser\My' `
  -NotAfter (Get-Date).AddYears($ValidYears) `
  -KeyExportPolicy Exportable

$pfxPath = Join-Path $OutDir 'WinVaultWarden-Signing.pfx'
$cerPath = Join-Path $OutDir 'WinVaultWarden-Signing.cer'
$b64Path = Join-Path $OutDir 'WinVaultWarden-Signing.pfx.base64'

$securePwd = ConvertTo-SecureString $Password -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePwd | Out-Null
Export-Certificate    -Cert $cert -FilePath $cerPath | Out-Null
[Convert]::ToBase64String([IO.File]::ReadAllBytes($pfxPath)) |
  Set-Content -Path $b64Path -NoNewline

# remove from the user store; the exported files are all we need
Remove-Item ("Cert:\CurrentUser\My\" + $cert.Thumbprint) -Force

Write-Host "Created:" -ForegroundColor Green
Write-Host "  $pfxPath"
Write-Host "  $cerPath"
Write-Host "  $b64Path"
Write-Host ""
Write-Host "Set GitHub repo secrets:" -ForegroundColor Cyan
Write-Host "  SIGNING_PFX_BASE64   = contents of $b64Path"
Write-Host "  SIGNING_PFX_PASSWORD = the -Password you passed"
