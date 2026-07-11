using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using App.Interop;
using Core.BrowserIntegration;
using Microsoft.Win32.SafeHandles;

namespace App.Services;

/// <summary>
/// App-side implementation of <see cref="IProcessIdentityReader"/> (Task 3). Mirrors the native
/// host reader: resolves a process' SID from its token, canonical image path, MSIX package family
/// (null when portable), and — for portable peers — Authenticode validity plus signer
/// SubjectPublicKeyInfo SHA-256. Every failure becomes a bare <see cref="UnauthorizedAccessException"/>
/// so no Win32 text, path, or signature detail leaks to callers or logs.
/// </summary>
internal sealed unsafe class WindowsAppProcessIdentityReader : IProcessIdentityReader
{
    public PeerIdentity ReadCurrentProcess()
    {
        try
        {
            // Pseudo-handle from GetCurrentProcess() must not be closed.
            return Read(NativeMethods.GetCurrentProcess(), (uint)Environment.ProcessId);
        }
        catch (Exception exception) when (exception is not UnauthorizedAccessException)
        {
            throw Denied();
        }
    }

    public PeerIdentity ReadProcess(uint processId)
    {
        try
        {
            var handle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == IntPtr.Zero)
                throw Denied();
            try
            {
                return Read(handle, processId);
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }
        catch (Exception exception) when (exception is not UnauthorizedAccessException)
        {
            throw Denied();
        }
    }

    public uint GetServerProcessId(NamedPipeClientStream pipe) =>
        QueryPipeProcessId(pipe.SafePipeHandle, server: true);

    public uint GetClientProcessId(NamedPipeServerStream pipe) =>
        QueryPipeProcessId(pipe.SafePipeHandle, server: false);

    private static uint QueryPipeProcessId(SafePipeHandle handle, bool server)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var raw = handle.DangerousGetHandle();
            uint pid;
            var ok = server
                ? NativeMethods.GetNamedPipeServerProcessId(raw, out pid)
                : NativeMethods.GetNamedPipeClientProcessId(raw, out pid);
            if (!ok)
                throw Denied();
            return pid;
        }
        finally
        {
            if (added)
                handle.DangerousRelease();
        }
    }

    private static PeerIdentity Read(IntPtr processHandle, uint processId)
    {
        var sid = ReadSid(processHandle);
        var path = ReadImagePath(processHandle);
        var packageFamily = ReadPackageFamilyName(processHandle);
        // Portable peers must carry an acceptable Authenticode signature (valid or self-signed with an
        // untrusted root — see AuthenticodeTrustDecision); packaged peers are judged by family instead.
        var authenticodeValid = packageFamily is null && VerifyAuthenticode(path);
        var signer = authenticodeValid ? GetSignerPublicKeySha256(path) : null;
        return new PeerIdentity(processId, sid, path, packageFamily, authenticodeValid, signer);
    }

    private static string ReadSid(IntPtr processHandle)
    {
        if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TOKEN_QUERY, out var token))
            throw Denied();
        try
        {
            NativeMethods.GetTokenInformation(token, NativeMethods.TokenUser, IntPtr.Zero, 0, out var length);
            if (length == 0)
                throw Denied();

            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenUser, buffer, length, out _))
                    throw Denied();
                // TOKEN_USER starts with SID_AND_ATTRIBUTES whose first field is the SID pointer.
                var sidPointer = Marshal.ReadIntPtr(buffer);
                return new SecurityIdentifier(sidPointer).Value;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    private static string ReadImagePath(IntPtr processHandle)
    {
        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        fixed (char* p = buffer)
        {
            if (!NativeMethods.QueryFullProcessImageName(processHandle, 0, p, ref size))
                throw Denied();
        }
        return Path.GetFullPath(new string(buffer, 0, (int)size));
    }

    private static string? ReadPackageFamilyName(IntPtr processHandle)
    {
        uint length = 0;
        var rc = NativeMethods.GetPackageFamilyName(processHandle, ref length, null);
        if (rc == NativeMethods.APPMODEL_ERROR_NO_PACKAGE)
            return null;
        if (rc != NativeMethods.ERROR_INSUFFICIENT_BUFFER && rc != NativeMethods.ERROR_SUCCESS)
            throw Denied();
        if (length == 0)
            return null;

        var buffer = new char[length];
        fixed (char* p = buffer)
        {
            rc = NativeMethods.GetPackageFamilyName(processHandle, ref length, p);
        }
        if (rc == NativeMethods.APPMODEL_ERROR_NO_PACKAGE)
            return null;
        if (rc != NativeMethods.ERROR_SUCCESS)
            throw Denied();

        var terminator = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, terminator >= 0 ? terminator : buffer.Length);
    }

    private static bool VerifyAuthenticode(string path)
    {
        var pathPointer = Marshal.StringToHGlobalUni(path);
        try
        {
            var fileInfo = new NativeMethods.WINTRUST_FILE_INFO
            {
                cbStruct = (uint)sizeof(NativeMethods.WINTRUST_FILE_INFO),
                pcwszFilePath = pathPointer,
            };
            var data = new NativeMethods.WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(NativeMethods.WINTRUST_DATA),
                dwUIChoice = NativeMethods.WTD_UI_NONE,
                fdwRevocationChecks = NativeMethods.WTD_REVOKE_NONE,
                dwUnionChoice = NativeMethods.WTD_CHOICE_FILE,
                dwStateAction = NativeMethods.WTD_STATEACTION_VERIFY,
                dwProvFlags = NativeMethods.WTD_REVOCATION_CHECK_NONE,
                pFile = (IntPtr)(&fileInfo),
            };
            var action = NativeMethods.WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var result = NativeMethods.WinVerifyTrust(IntPtr.Zero, ref action, (IntPtr)(&data));

            data.dwStateAction = NativeMethods.WTD_STATEACTION_CLOSE;
            NativeMethods.WinVerifyTrust(IntPtr.Zero, ref action, (IntPtr)(&data));
            // Accept S_OK or exactly CERT_E_UNTRUSTEDROOT (portable signer-pinning); reject unsigned,
            // tampered, expired, revoked, unknown-subject and every other status fail-closed.
            return AuthenticodeTrustDecision.IsAcceptable(result);
        }
        finally
        {
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    private static string GetSignerPublicKeySha256(string path)
    {
        // CreateFromSignedFile is the supported way to read the Authenticode signer certificate;
        // its SYSLIB0057 obsoletion has no non-obsolete equivalent for signed PE files.
#pragma warning disable SYSLIB0057
        using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        byte[] spki;
        using (var rsa = certificate.GetRSAPublicKey())
        {
            if (rsa is not null)
            {
                spki = rsa.ExportSubjectPublicKeyInfo();
            }
            else
            {
                using var ecdsa = certificate.GetECDsaPublicKey()
                    ?? throw new CryptographicException("Unsupported signer public key.");
                spki = ecdsa.ExportSubjectPublicKeyInfo();
            }
        }
        return Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
    }

    private static UnauthorizedAccessException Denied() =>
        new("Unable to verify the pipe peer process identity.");
}
