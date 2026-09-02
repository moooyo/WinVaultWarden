using System.Runtime.InteropServices;
using System.Text.Json;
using Core.Models;
using Core.Passkeys;
using Core.Services;

namespace App.Services;

public sealed partial class WindowsWebAuthnTwoFactorService : IWebAuthnTwoFactorRegistrationService
{
    private const uint RpEntityVersion = 1;
    private const uint UserEntityVersion = 1;
    private const uint CoseCredentialParameterVersion = 1;
    private const uint CredentialExVersion = 1;
    private const uint MakeCredentialOptionsVersion = 3;
    private const uint AuthenticatorAttachmentPlatform = 1;
    private const uint AuthenticatorAttachmentCrossPlatform = 2;
    private const uint AttestationNone = 1;
    private const uint AttestationIndirect = 2;
    private const uint AttestationDirect = 3;
    private const uint TransportUsb = 0x00000001;
    private const uint TransportNfc = 0x00000002;
    private const uint TransportBle = 0x00000004;
    private const uint TransportInternal = 0x00000010;
    private const uint TransportHybrid = 0x00000020;
    private const uint TransportSmartCard = 0x00000040;
    private const int MaxRegistrationChallengeBytes = 1024;
    private const int MaxRegistrationCredentialCount = 64;
    private const int MaxRegistrationCredentialIdBytes = 4096;
    private const int MaxRegistrationPartBytes = 4 * 1024 * 1024;

    public async Task<WebAuthnRegistrationDeviceResponse> CreateCredentialAsync(
        WebAuthnRegistrationChallenge challenge,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ValidateRegistrationChallenge(challenge);
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
                () => CreateCredentialCore(windowHandle, challenge, ct),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static WebAuthnRegistrationDeviceResponse CreateCredentialCore(
        nint windowHandle,
        WebAuthnRegistrationChallenge challenge,
        CancellationToken ct)
    {
        using var allocations = new NativeAllocations();
        var clientDataJson = BuildRegistrationClientDataJson(challenge);
        var clientData = new WebAuthnClientData
        {
            Version = ClientDataVersion,
            ClientDataJsonLength = checked((uint)clientDataJson.Length),
            ClientDataJson = allocations.Bytes(clientDataJson),
            HashAlgorithm = allocations.String("SHA-256"),
        };
        var relyingParty = new WebAuthnRpEntityInformation
        {
            Version = RpEntityVersion,
            Id = allocations.String(challenge.RpId),
            Name = allocations.String(challenge.RpName),
        };
        var user = new WebAuthnUserEntityInformation
        {
            Version = UserEntityVersion,
            IdLength = checked((uint)challenge.UserId.Length),
            Id = allocations.Bytes(challenge.UserId),
            Name = allocations.String(challenge.UserName),
            DisplayName = allocations.String(challenge.UserDisplayName),
        };
        var parameters = BuildCoseCredentialParameters(
            challenge.PublicKeyCredentialParameters,
            allocations);
        var excludeCredentials = BuildExcludeCredentialList(
            challenge.ExcludeCredentials,
            allocations);
        var cancellationId = GetCancellationId();
        var options = new WebAuthnMakeCredentialOptionsV3
        {
            Version = MakeCredentialOptionsVersion,
            TimeoutMilliseconds = checked((uint)challenge.TimeoutMilliseconds),
            AuthenticatorAttachment = MapAuthenticatorAttachment(challenge.AuthenticatorAttachment),
            RequireResidentKey = RequiresResidentKey(challenge) ? 1 : 0,
            UserVerificationRequirement = MapRegistrationUserVerification(challenge.UserVerification),
            AttestationConveyancePreference = MapAttestation(challenge.Attestation),
            CancellationId = allocations.Structure(cancellationId),
            ExcludeCredentialList = excludeCredentials,
        };

        using var cancellation = ct.Register(
            static state => CancelNativeOperation((Guid)state!),
            cancellationId);
        ct.ThrowIfCancellationRequested();
        _ = SetForegroundWindow(windowHandle);

        nint attestationPointer = nint.Zero;
        try
        {
            var result = WebAuthNAuthenticatorMakeCredential(
                windowHandle,
                in relyingParty,
                in user,
                in parameters,
                in clientData,
                in options,
                out attestationPointer);
            if (result < 0)
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);
                if (result is HResultUserCancelled or HResultOperationCancelled)
                    throw new WebAuthnTwoFactorCancelledException();
                throw new WebAuthnTwoFactorException(
                    $"Windows WebAuthn failed with HRESULT 0x{result:X8}.");
            }
            if (attestationPointer == nint.Zero)
                throw new WebAuthnTwoFactorException("Windows WebAuthn returned no attestation.");

            var attestation = Marshal.PtrToStructure<WebAuthnCredentialAttestationV3>(attestationPointer);
            if (attestation.Version is < 1 or > 64)
                throw InvalidRegistrationResult();

            var credentialId = CopyRegistrationPart(
                attestation.CredentialId,
                attestation.CredentialIdLength,
                1,
                MaxRegistrationCredentialIdBytes);
            var attestationObject = CopyRegistrationPart(
                attestation.AttestationObject,
                attestation.AttestationObjectLength,
                1,
                MaxRegistrationPartBytes);
            return new WebAuthnRegistrationDeviceResponse(
                credentialId,
                attestationObject,
                clientDataJson,
                MapTransports(attestation.Version >= 3 ? attestation.UsedTransport : 0));
        }
        finally
        {
            if (attestationPointer != nint.Zero)
                WebAuthNFreeCredentialAttestation(attestationPointer);
        }
    }

    private static byte[] BuildRegistrationClientDataJson(WebAuthnRegistrationChallenge challenge)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "webauthn.create");
            writer.WriteString("challenge", challenge.Challenge);
            writer.WriteString("origin", challenge.Origin);
            writer.WriteBoolean("crossOrigin", false);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static WebAuthnCoseCredentialParameters BuildCoseCredentialParameters(
        IReadOnlyList<WebAuthnPublicKeyCredentialParameter> source,
        NativeAllocations allocations)
    {
        var itemSize = Marshal.SizeOf<WebAuthnCoseCredentialParameter>();
        var items = allocations.Allocate(checked(itemSize * source.Count));
        var credentialType = allocations.String("public-key");
        for (var index = 0; index < source.Count; index++)
        {
            var item = new WebAuthnCoseCredentialParameter
            {
                Version = CoseCredentialParameterVersion,
                CredentialType = credentialType,
                Algorithm = source[index].Alg,
            };
            Marshal.StructureToPtr(item, items + index * itemSize, fDeleteOld: false);
        }

        return new WebAuthnCoseCredentialParameters
        {
            CredentialParameterCount = checked((uint)source.Count),
            CredentialParameters = items,
        };
    }

    private static nint BuildExcludeCredentialList(
        IReadOnlyList<WebAuthnCredentialDescriptor> source,
        NativeAllocations allocations)
    {
        if (source.Count == 0)
            return nint.Zero;

        var pointers = allocations.Allocate(checked(IntPtr.Size * source.Count));
        var credentialType = allocations.String("public-key");
        for (var index = 0; index < source.Count; index++)
        {
            var descriptor = source[index];
            var credential = new WebAuthnCredentialEx
            {
                Version = CredentialExVersion,
                IdLength = checked((uint)descriptor.Id.Length),
                Id = allocations.Bytes(descriptor.Id),
                CredentialType = credentialType,
                Transports = MapTransports(descriptor.Transports),
            };
            var credentialPointer = allocations.Structure(credential);
            Marshal.WriteIntPtr(pointers, checked(index * IntPtr.Size), credentialPointer);
        }

        return allocations.Structure(new WebAuthnCredentialList
        {
            CredentialCount = checked((uint)source.Count),
            Credentials = pointers,
        });
    }

    private static void ValidateRegistrationChallenge(WebAuthnRegistrationChallenge challenge)
    {
        if (challenge.IsConsumed)
            throw new WebAuthnTwoFactorException("The WebAuthn registration challenge has already been consumed.");

        byte[] challengeBytes;
        try
        {
            challengeBytes = WebAuthnCrypto.DecodeBase64Url(challenge.Challenge);
        }
        catch (FormatException ex)
        {
            throw new WebAuthnTwoFactorException("The WebAuthn registration challenge is invalid.", ex);
        }
        if (challengeBytes.Length is < 16 or > MaxRegistrationChallengeBytes
            || !string.Equals(
                WebAuthnCrypto.EncodeBase64Url(challengeBytes),
                challenge.Challenge,
                StringComparison.Ordinal))
        {
            throw new WebAuthnTwoFactorException("The WebAuthn registration challenge is invalid.");
        }

        if (!Uri.TryCreate(challenge.Origin, UriKind.Absolute, out var origin)
            || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment)
            || origin.AbsolutePath != "/"
            || (origin.Scheme != Uri.UriSchemeHttps
                && !(origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback))
            || !string.Equals(origin.IdnHost, challenge.RpId, StringComparison.OrdinalIgnoreCase))
        {
            throw new WebAuthnTwoFactorException("The WebAuthn relying party is invalid.");
        }

        ValidateNativeString(challenge.RpId, 253);
        ValidateNativeString(challenge.RpName, 1024);
        ValidateNativeString(challenge.UserName, 1024);
        ValidateNativeString(challenge.UserDisplayName, 1024);
        if (challenge.UserId.Length is < 1 or > 64)
            throw new WebAuthnTwoFactorException("The WebAuthn user identifier is invalid.");
        if (challenge.TimeoutMilliseconds is < 15_000 or > 120_000)
            throw new WebAuthnTwoFactorException("The WebAuthn timeout is invalid.");
        if (challenge.PublicKeyCredentialParameters.Count is < 1 or > 64)
            throw new WebAuthnTwoFactorException("The WebAuthn credential parameters are invalid.");
        foreach (var parameter in challenge.PublicKeyCredentialParameters)
        {
            if (parameter is null
                || !string.Equals(parameter.Type, "public-key", StringComparison.Ordinal)
                || parameter.Alg == 0)
            {
                throw new WebAuthnTwoFactorException("The WebAuthn credential parameters are invalid.");
            }
        }
        if (challenge.ExcludeCredentials.Count > MaxRegistrationCredentialCount)
            throw new WebAuthnTwoFactorException("The WebAuthn excluded credential list is invalid.");
        foreach (var descriptor in challenge.ExcludeCredentials)
        {
            if (descriptor is null
                || !string.Equals(descriptor.Type, "public-key", StringComparison.Ordinal)
                || descriptor.Id.Length is < 1 or > MaxRegistrationCredentialIdBytes)
            {
                throw new WebAuthnTwoFactorException("The WebAuthn excluded credential list is invalid.");
            }
            _ = MapTransports(descriptor.Transports);
        }

        _ = MapAuthenticatorAttachment(challenge.AuthenticatorAttachment);
        _ = RequiresResidentKey(challenge);
        _ = MapRegistrationUserVerification(challenge.UserVerification);
        _ = MapAttestation(challenge.Attestation);
    }

    private static void ValidateNativeString(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Contains('\0'))
        {
            throw new WebAuthnTwoFactorException("The WebAuthn registration challenge is invalid.");
        }
    }

    private static uint MapAuthenticatorAttachment(string? value) => value switch
    {
        null or "" => AuthenticatorAttachmentAny,
        "platform" => AuthenticatorAttachmentPlatform,
        "cross-platform" => AuthenticatorAttachmentCrossPlatform,
        _ => throw new WebAuthnTwoFactorException("The WebAuthn authenticator attachment is invalid."),
    };

    private static bool RequiresResidentKey(WebAuthnRegistrationChallenge challenge)
    {
        if (challenge.ResidentKey is not ("required" or "preferred" or "discouraged"))
            throw new WebAuthnTwoFactorException("The WebAuthn resident-key requirement is invalid.");
        if (challenge.RequireResidentKey && challenge.ResidentKey != "required")
            throw new WebAuthnTwoFactorException("The WebAuthn resident-key requirements conflict.");
        return challenge.RequireResidentKey || challenge.ResidentKey == "required";
    }

    private static uint MapRegistrationUserVerification(string value) => value switch
    {
        "required" => UserVerificationRequired,
        "preferred" => UserVerificationPreferred,
        "discouraged" => UserVerificationDiscouraged,
        _ => throw new WebAuthnTwoFactorException("The WebAuthn user-verification requirement is invalid."),
    };

    private static uint MapAttestation(string value) => value switch
    {
        "none" => AttestationNone,
        "indirect" => AttestationIndirect,
        "direct" => AttestationDirect,
        _ => throw new WebAuthnTwoFactorException("The WebAuthn attestation preference is invalid."),
    };

    private static uint MapTransports(IReadOnlyList<string> transports)
    {
        uint result = 0;
        foreach (var transport in transports)
        {
            result |= transport switch
            {
                "usb" => TransportUsb,
                "nfc" => TransportNfc,
                "ble" => TransportBle,
                "internal" => TransportInternal,
                "hybrid" => TransportHybrid,
                "smart-card" => TransportSmartCard,
                _ => throw new WebAuthnTwoFactorException("The WebAuthn credential transport is invalid."),
            };
        }
        return result;
    }

    private static IReadOnlyList<string> MapTransports(uint transports)
    {
        var result = new List<string>(6);
        if ((transports & TransportUsb) != 0)
            result.Add("usb");
        if ((transports & TransportNfc) != 0)
            result.Add("nfc");
        if ((transports & TransportBle) != 0)
            result.Add("ble");
        if ((transports & TransportInternal) != 0)
            result.Add("internal");
        if ((transports & TransportHybrid) != 0)
            result.Add("hybrid");
        if ((transports & TransportSmartCard) != 0)
            result.Add("smart-card");
        return result.AsReadOnly();
    }

    private static byte[] CopyRegistrationPart(
        nint source,
        uint length,
        int minimumLength,
        int maximumLength)
    {
        if (length < minimumLength || length > maximumLength || source == nint.Zero)
            throw InvalidRegistrationResult();
        var result = new byte[checked((int)length)];
        Marshal.Copy(source, result, 0, result.Length);
        return result;
    }

    private static WebAuthnTwoFactorException InvalidRegistrationResult() =>
        new("Windows WebAuthn returned an invalid attestation.");

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnRpEntityInformation
    {
        public uint Version;
        public nint Id;
        public nint Name;
        public nint Icon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnUserEntityInformation
    {
        public uint Version;
        public uint IdLength;
        public nint Id;
        public nint Name;
        public nint Icon;
        public nint DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnCoseCredentialParameter
    {
        public uint Version;
        public nint CredentialType;
        public int Algorithm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnCoseCredentialParameters
    {
        public uint CredentialParameterCount;
        public nint CredentialParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnCredentialEx
    {
        public uint Version;
        public uint IdLength;
        public nint Id;
        public nint CredentialType;
        public uint Transports;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnCredentialList
    {
        public uint CredentialCount;
        public nint Credentials;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnMakeCredentialOptionsV3
    {
        public uint Version;
        public uint TimeoutMilliseconds;
        public WebAuthnCredentials CredentialList;
        public WebAuthnExtensions Extensions;
        public uint AuthenticatorAttachment;
        public int RequireResidentKey;
        public uint UserVerificationRequirement;
        public uint AttestationConveyancePreference;
        public uint Flags;
        public nint CancellationId;
        public nint ExcludeCredentialList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnCredentialAttestationV3
    {
        public uint Version;
        public nint FormatType;
        public uint AuthenticatorDataLength;
        public nint AuthenticatorData;
        public uint AttestationLength;
        public nint Attestation;
        public uint AttestationDecodeType;
        public nint AttestationDecode;
        public uint AttestationObjectLength;
        public nint AttestationObject;
        public uint CredentialIdLength;
        public nint CredentialId;
        public WebAuthnExtensions Extensions;
        public uint UsedTransport;
    }

    [LibraryImport("webauthn.dll")]
    private static partial int WebAuthNAuthenticatorMakeCredential(
        nint windowHandle,
        in WebAuthnRpEntityInformation relyingPartyInformation,
        in WebAuthnUserEntityInformation userInformation,
        in WebAuthnCoseCredentialParameters credentialParameters,
        in WebAuthnClientData clientData,
        in WebAuthnMakeCredentialOptionsV3 options,
        out nint attestation);

    [LibraryImport("webauthn.dll")]
    private static partial void WebAuthNFreeCredentialAttestation(nint attestation);
}
