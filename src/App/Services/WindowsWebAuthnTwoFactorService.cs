using System.Runtime.InteropServices;
using Core.Services;

namespace App.Services;

public interface IWebAuthnWindowHandleProvider
{
    nint GetWindowHandle();
}

public sealed partial class WindowsWebAuthnTwoFactorService : IWebAuthnTwoFactorService
{
    private const uint ClientDataVersion = 1;
    private const uint CredentialVersion = 1;
    private const uint GetAssertionOptionsVersion = 3;
    private const uint AuthenticatorAttachmentAny = 0;
    private const uint UserVerificationRequired = 1;
    private const uint UserVerificationPreferred = 2;
    private const uint UserVerificationDiscouraged = 3;
    private const int HResultUserCancelled = unchecked((int)0x80090036);
    private const int HResultOperationCancelled = unchecked((int)0x800704C7);
    private const int MaxCredentialIdBytes = 4096;
    private const int MaxAuthenticatorDataBytes = 1024 * 1024;
    private const int MaxSignatureBytes = 1024 * 1024;
    private const int MaxUserHandleBytes = 64;

    private readonly IWebAuthnWindowHandleProvider _windowHandle;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsWebAuthnTwoFactorService(IWebAuthnWindowHandleProvider windowHandle) =>
        _windowHandle = windowHandle ?? throw new ArgumentNullException(nameof(windowHandle));

    public bool IsSupported
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return false;
            try
            {
                return WebAuthNGetApiVersionNumber() >= 1;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    public async Task<string> AuthenticateAsync(
        string providerDataJson,
        Core.Models.ServerEndpoints endpoints,
        CancellationToken ct = default)
    {
        var challenge = WebAuthnTwoFactorProtocol.ParseChallenge(providerDataJson, endpoints);
        if (!IsSupported)
            throw new WebAuthnTwoFactorException("Windows WebAuthn is not available.");
        var windowHandle = _windowHandle.GetWindowHandle();
        if (windowHandle == 0)
            throw new WebAuthnTwoFactorException("The WebAuthn parent window is unavailable.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            return await Task.Run(
                () => AuthenticateCore(windowHandle, challenge, ct),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string AuthenticateCore(
        nint windowHandle,
        WebAuthnTwoFactorChallenge challenge,
        CancellationToken ct)
    {
        using var allocations = new NativeAllocations();
        var clientDataJson = WebAuthnTwoFactorProtocol.BuildClientDataJson(challenge);
        var clientData = new WebAuthnClientData
        {
            Version = ClientDataVersion,
            ClientDataJsonLength = checked((uint)clientDataJson.Length),
            ClientDataJson = allocations.Bytes(clientDataJson),
            HashAlgorithm = allocations.String("SHA-256"),
        };

        var credentials = BuildCredentials(challenge.AllowCredentialIds, allocations);
        var appIdResult = challenge.U2fAppId is null ? nint.Zero : allocations.Int32(0);
        var cancellationId = GetCancellationId();
        var options = new WebAuthnGetAssertionOptionsV3
        {
            Version = GetAssertionOptionsVersion,
            TimeoutMilliseconds = checked((uint)challenge.TimeoutMilliseconds),
            CredentialList = credentials,
            AuthenticatorAttachment = AuthenticatorAttachmentAny,
            UserVerificationRequirement = MapUserVerification(challenge.UserVerification),
            U2fAppId = challenge.U2fAppId is null ? nint.Zero : allocations.String(challenge.U2fAppId),
            U2fAppIdUsed = appIdResult,
            CancellationId = allocations.Structure(cancellationId),
        };

        using var cancellation = ct.Register(
            static state => CancelNativeOperation((Guid)state!),
            cancellationId);
        ct.ThrowIfCancellationRequested();
        _ = SetForegroundWindow(windowHandle);

        nint assertionPointer = nint.Zero;
        try
        {
            var result = WebAuthNAuthenticatorGetAssertion(
                windowHandle,
                allocations.String(challenge.RpId),
                in clientData,
                in options,
                out assertionPointer);
            if (result < 0)
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);
                if (result is HResultUserCancelled or HResultOperationCancelled)
                    throw new WebAuthnTwoFactorCancelledException();
                throw new WebAuthnTwoFactorException(
                    $"Windows WebAuthn failed with HRESULT 0x{result:X8}.");
            }
            if (assertionPointer == nint.Zero)
                throw new WebAuthnTwoFactorException("Windows WebAuthn returned no assertion.");

            var assertion = Marshal.PtrToStructure<WebAuthnAssertionV1>(assertionPointer);
            var managed = new WebAuthnTwoFactorAssertion(
                Copy(assertion.Credential.Id, assertion.Credential.IdLength, 1, MaxCredentialIdBytes),
                Copy(assertion.AuthenticatorData, assertion.AuthenticatorDataLength, 37, MaxAuthenticatorDataBytes),
                clientDataJson,
                Copy(assertion.Signature, assertion.SignatureLength, 1, MaxSignatureBytes),
                Copy(assertion.UserId, assertion.UserIdLength, 0, MaxUserHandleBytes),
                challenge.U2fAppId is null ? null : Marshal.ReadInt32(appIdResult) != 0);
            return WebAuthnTwoFactorProtocol.SerializeToken(managed);
        }
        finally
        {
            if (assertionPointer != nint.Zero)
                WebAuthNFreeAssertion(assertionPointer);
        }
    }

    private static WebAuthnCredentials BuildCredentials(
        IReadOnlyList<byte[]> credentialIds,
        NativeAllocations allocations)
    {
        var size = Marshal.SizeOf<WebAuthnCredential>();
        var array = allocations.Allocate(checked(size * credentialIds.Count));
        var credentialType = allocations.String("public-key");
        for (var index = 0; index < credentialIds.Count; index++)
        {
            var credentialId = credentialIds[index];
            var credential = new WebAuthnCredential
            {
                Version = CredentialVersion,
                IdLength = checked((uint)credentialId.Length),
                Id = allocations.Bytes(credentialId),
                CredentialType = credentialType,
            };
            Marshal.StructureToPtr(credential, array + index * size, fDeleteOld: false);
        }

        return new WebAuthnCredentials
        {
            CredentialCount = checked((uint)credentialIds.Count),
            Credentials = array,
        };
    }

    private static uint MapUserVerification(string value) => value switch
    {
        "required" => UserVerificationRequired,
        "preferred" => UserVerificationPreferred,
        _ => UserVerificationDiscouraged,
    };

    private static Guid GetCancellationId()
    {
        var result = WebAuthNGetCancellationId(out var cancellationId);
        if (result < 0 || cancellationId == Guid.Empty)
        {
            throw new WebAuthnTwoFactorException(
                $"Windows WebAuthn could not create a cancellation ID (0x{result:X8}).");
        }
        return cancellationId;
    }

    private static void CancelNativeOperation(Guid cancellationId)
    {
        try
        {
            _ = WebAuthNCancelCurrentOperation(in cancellationId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private static byte[] Copy(
        nint source,
        uint length,
        int minimumLength,
        int maximumLength)
    {
        if (length > maximumLength || length < minimumLength)
            throw new WebAuthnTwoFactorException("Windows WebAuthn returned an invalid assertion.");
        if (length == 0)
            return Array.Empty<byte>();
        if (source == nint.Zero)
            throw new WebAuthnTwoFactorException("Windows WebAuthn returned an invalid assertion.");

        var result = new byte[checked((int)length)];
        Marshal.Copy(source, result, 0, result.Length);
        return result;
    }

    private sealed class NativeAllocations : IDisposable
    {
        private readonly List<nint> _allocations = [];

        public nint Allocate(int byteCount)
        {
            if (byteCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(byteCount));
            var pointer = Marshal.AllocHGlobal(byteCount);
            _allocations.Add(pointer);
            return pointer;
        }

        public nint Bytes(byte[] value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var pointer = Allocate(value.Length);
            Marshal.Copy(value, 0, pointer, value.Length);
            return pointer;
        }

        public nint String(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var pointer = Marshal.StringToHGlobalUni(value);
            _allocations.Add(pointer);
            return pointer;
        }

        public nint Int32(int value)
        {
            var pointer = Allocate(sizeof(int));
            Marshal.WriteInt32(pointer, value);
            return pointer;
        }

        public nint Structure<T>(T value) where T : struct
        {
            var pointer = Allocate(Marshal.SizeOf<T>());
            Marshal.StructureToPtr(value, pointer, fDeleteOld: false);
            return pointer;
        }

        public void Dispose()
        {
            for (var index = _allocations.Count - 1; index >= 0; index--)
                Marshal.FreeHGlobal(_allocations[index]);
            _allocations.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnCredential
    {
        public uint Version;
        public uint IdLength;
        public nint Id;
        public nint CredentialType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnCredentials
    {
        public uint CredentialCount;
        public nint Credentials;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnExtensions
    {
        public uint ExtensionCount;
        public nint Extensions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnClientData
    {
        public uint Version;
        public uint ClientDataJsonLength;
        public nint ClientDataJson;
        public nint HashAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnGetAssertionOptionsV3
    {
        public uint Version;
        public uint TimeoutMilliseconds;
        public WebAuthnCredentials CredentialList;
        public WebAuthnExtensions Extensions;
        public uint AuthenticatorAttachment;
        public uint UserVerificationRequirement;
        public uint Flags;
        public nint U2fAppId;
        public nint U2fAppIdUsed;
        public nint CancellationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnAssertionV1
    {
        public uint Version;
        public uint AuthenticatorDataLength;
        public nint AuthenticatorData;
        public uint SignatureLength;
        public nint Signature;
        public WebAuthnCredential Credential;
        public uint UserIdLength;
        public nint UserId;
    }

    [LibraryImport("webauthn.dll")]
    private static partial uint WebAuthNGetApiVersionNumber();

    [LibraryImport("webauthn.dll")]
    private static partial int WebAuthNGetCancellationId(out Guid cancellationId);

    [LibraryImport("webauthn.dll")]
    private static partial int WebAuthNCancelCurrentOperation(in Guid cancellationId);

    [LibraryImport("webauthn.dll")]
    private static partial int WebAuthNAuthenticatorGetAssertion(
        nint windowHandle,
        nint relyingPartyId,
        in WebAuthnClientData clientData,
        in WebAuthnGetAssertionOptionsV3 options,
        out nint assertion);

    [LibraryImport("webauthn.dll")]
    private static partial void WebAuthNFreeAssertion(nint assertion);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint windowHandle);
}
