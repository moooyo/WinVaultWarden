#Requires -Version 7.0
<#
.SYNOPSIS
    Verify portable signer-pinning between the WinVaultWarden App and native host.

.DESCRIPTION
    Diagnostic aid that mirrors the in-process check performed by
    WindowsProcessIdentityReader / WindowsAppProcessIdentityReader:

      1. WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2) on each binary and
         apply Core.BrowserIntegration.AuthenticodeTrustDecision: accept only
         S_OK (0) or exactly CERT_E_UNTRUSTEDROOT (0x800B0109).
      2. For each acceptable binary, read the Authenticode signer certificate and
         compute SHA-256 of its SubjectPublicKeyInfo (lower-case hex).
      3. Require both binaries acceptable and their signer SPKI hashes equal.

    This does NOT install or trust any certificate. It only confirms the two
    portable binaries are pinned to the same signer, exactly as the app enforces
    at runtime. It is not a substitute for the app's in-process authorization —
    the app also checks token SID and canonical path over the live pipe.

    Requires PowerShell 7+ (uses ExportSubjectPublicKeyInfo, as the app does).

.PARAMETER AppPath
    Path to App.exe (defaults to this repo's expected portable layout sibling).

.PARAMETER HostPath
    Path to BrowserNativeHost.exe.

.EXAMPLE
    .\verify-signer-pinning.ps1 -AppPath D:\WinVaultWarden\App.exe -HostPath D:\WinVaultWarden\BrowserNativeHost.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $AppPath,
    [Parameter(Mandatory)] [string] $HostPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# S_OK or exactly CERT_E_UNTRUSTEDROOT — must stay in sync with
# Core.BrowserIntegration.AuthenticodeTrustDecision.IsAcceptable.
$SOk = 0
$CertUntrustedRoot = [int]0x800B0109

if (-not ('WinVaultWarden.WinTrust' -as [type])) {
    Add-Type -Namespace 'WinVaultWarden' -Name 'WinTrust' -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct WINTRUST_FILE_INFO { public uint cbStruct; public System.IntPtr pcwszFilePath; public System.IntPtr hFile; public System.IntPtr pgKnownSubject; }

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct WINTRUST_DATA {
    public uint cbStruct; public System.IntPtr pPolicyCallbackData; public System.IntPtr pSIPClientData;
    public uint dwUIChoice; public uint fdwRevocationChecks; public uint dwUnionChoice; public System.IntPtr pFile;
    public uint dwStateAction; public System.IntPtr hWVTStateData; public System.IntPtr pwszURLReference;
    public uint dwProvFlags; public uint dwUIContext; public System.IntPtr pSignatureSettings;
}

[System.Runtime.InteropServices.DllImport("wintrust.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern int WinVerifyTrust(System.IntPtr hwnd, ref System.Guid pgActionID, System.IntPtr pWVTData);

public static int Verify(string path) {
    var action = new System.Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
    var file = new WINTRUST_FILE_INFO();
    file.cbStruct = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(WINTRUST_FILE_INFO));
    file.pcwszFilePath = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(path);
    System.IntPtr pFile = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
    System.IntPtr pData = System.IntPtr.Zero;
    try {
        System.Runtime.InteropServices.Marshal.StructureToPtr(file, pFile, false);
        var data = new WINTRUST_DATA();
        data.cbStruct = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(WINTRUST_DATA));
        data.dwUIChoice = 2;            // WTD_UI_NONE
        data.fdwRevocationChecks = 0;   // WTD_REVOKE_NONE
        data.dwUnionChoice = 1;         // WTD_CHOICE_FILE
        data.dwStateAction = 1;         // WTD_STATEACTION_VERIFY
        data.dwProvFlags = 0x00000010;  // WTD_REVOCATION_CHECK_NONE
        data.pFile = pFile;
        pData = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf(typeof(WINTRUST_DATA)));
        System.Runtime.InteropServices.Marshal.StructureToPtr(data, pData, false);
        int result = WinVerifyTrust(System.IntPtr.Zero, ref action, pData);
        data.dwStateAction = 2;         // WTD_STATEACTION_CLOSE
        System.Runtime.InteropServices.Marshal.StructureToPtr(data, pData, false);
        WinVerifyTrust(System.IntPtr.Zero, ref action, pData);
        return result;
    } finally {
        if (pData != System.IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(pData);
        System.Runtime.InteropServices.Marshal.FreeHGlobal(pFile);
        System.Runtime.InteropServices.Marshal.FreeHGlobal(file.pcwszFilePath);
    }
}
'@
}

function Get-SignerSpkiSha256 {
    param([string] $Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full)) { throw "File not found: $full" }

    $hr = [WinVaultWarden.WinTrust]::Verify($full)
    $acceptable = ($hr -eq $SOk) -or ($hr -eq $CertUntrustedRoot)
    $status = switch ($hr) {
        $SOk { 'S_OK (trusted)' }
        $CertUntrustedRoot { 'CERT_E_UNTRUSTEDROOT (self-signed / untrusted root)' }
        default { ('0x{0:X8}' -f $hr) }
    }
    if (-not $acceptable) {
        return [pscustomobject]@{ Path = $full; Acceptable = $false; Status = $status; Spki = $null }
    }

    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($full)
    $cert2 = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cert)
    try {
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($cert2)
        if ($rsa) { $spkiBytes = $rsa.ExportSubjectPublicKeyInfo(); $rsa.Dispose() }
        else {
            $ec = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPublicKey($cert2)
            if (-not $ec) { throw 'Unsupported signer public key.' }
            $spkiBytes = $ec.ExportSubjectPublicKeyInfo(); $ec.Dispose()
        }
    } finally { $cert2.Dispose() }

    $hash = [System.Security.Cryptography.SHA256]::HashData($spkiBytes)
    $hex = ([System.BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
    return [pscustomobject]@{ Path = $full; Acceptable = $true; Status = $status; Spki = $hex }
}

$app = Get-SignerSpkiSha256 -Path $AppPath
$hostBin = Get-SignerSpkiSha256 -Path $HostPath

Write-Host 'WinVaultWarden portable signer-pinning check' -ForegroundColor Cyan
foreach ($b in @($app, $hostBin)) {
    Write-Host ("  {0}" -f $b.Path)
    Write-Host ("    WinVerifyTrust : {0}" -f $b.Status)
    Write-Host ("    signer SPKI    : {0}" -f ($(if ($b.Spki) { $b.Spki } else { '<not signed / rejected>' })))
}

if (-not $app.Acceptable -or -not $hostBin.Acceptable) {
    Write-Host 'REJECTED: one or both binaries lack an acceptable Authenticode signature.' -ForegroundColor Red
    exit 1
}
if ($app.Spki -ne $hostBin.Spki) {
    Write-Host 'REJECTED: signer public keys differ — binaries are not pinned to the same signer.' -ForegroundColor Red
    exit 1
}

Write-Host 'OK: both binaries signed by the same signer; portable signer-pinning would allow the bridge.' -ForegroundColor Green
exit 0
