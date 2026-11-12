using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Core.WindowsPasskeys;
using Microsoft.Win32;

namespace App.Services;

public sealed record WindowsPasskeyProviderStatus(bool Supported, bool Registered, bool Enabled, string Description);

/// <summary>Exact Win32 ABI declarations from Microsoft's webauthnplugin.h.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class WindowsPasskeyPlatformApi
{
    private const int NotFound = unchecked((int)0x80090011);
    private static readonly Lock ModuleGate = new();
    private static nint _loadedModule;
    private static readonly string[] RequiredExports =
    [
        "WebAuthNPluginAddAuthenticator", "WebAuthNPluginRemoveAuthenticator",
        "WebAuthNPluginFreeAddAuthenticatorResponse",
        "WebAuthNPluginGetAuthenticatorState", "WebAuthNPluginGetOperationSigningPublicKey",
        "WebAuthNPluginFreePublicKeyResponse", "WebAuthNDecodeMakeCredentialRequest",
        "WebAuthNDecodeGetAssertionRequest", "WebAuthNFreeDecodedMakeCredentialRequest",
        "WebAuthNFreeDecodedGetAssertionRequest", "WebAuthNPluginAuthenticatorAddCredentials",
        "WebAuthNPluginAuthenticatorRemoveAllCredentials",
    ];

    static WindowsPasskeyPlatformApi()
    {
        // Microsoft's plugin reference lists WebAuthnPlugin.dll, while its matching
        // sample resolves these exports from webauthn.dll. Resolve the real System32
        // module containing the complete ABI and retain it for P/Invoke thunk lifetime.
        NativeLibrary.SetDllImportResolver(typeof(WindowsPasskeyPlatformApi).Assembly,
            (name, assembly, searchPath) => name == "WinVaultWarden.WebAuthnPlugin" && TryLoadModule()
                ? _loadedModule : 0);
    }

    private static bool TryLoadModule()
    {
        lock (ModuleGate)
        {
            if (_loadedModule != 0) return true;
            foreach (var name in new[] { "webauthnplugin.dll", "webauthn.dll" })
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);
                if (!NativeLibrary.TryLoad(path, out var module)) continue;
                if (RequiredExports.All(export => NativeLibrary.TryGetExport(module, export, out _)))
                {
                    _loadedModule = module;
                    return true;
                }
                NativeLibrary.Free(module);
            }
            return false;
        }
    }

    internal static WindowsPasskeyProviderStatus GetStatus()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            return new(false, false, false, "Windows 11 24H2 or later is required.");
        if (!TryLoadModule())
            return new(false, false, false, "Install Windows updates that include passkey manager plugin support.");
        var id = WindowsPasskeyIdentity.ClassId;
        var result = WebAuthNPluginGetAuthenticatorState(in id, out var state);
        if (result == NotFound)
            return new(true, false, false, "The Windows passkey provider is not registered.");
        if (result < 0)
            return new(true, false, false, $"Windows could not read provider state (0x{result:X8}).");
        return new(true, true, state == 1, state == 1
            ? "Enabled in Windows passkey settings." : "Registered. Enable WinVaultWarden in Windows passkey settings.");
    }

    internal static void Register(string executablePath, bool packaged)
    {
        var status = GetStatus();
        if (!status.Supported)
            throw new PlatformNotSupportedException(status.Description);
        var id = WindowsPasskeyIdentity.ClassId;
        string? ownedRegistration = null;
        if (!packaged)
        {
            var fullPath = Path.GetFullPath(executablePath);
            if (!File.Exists(fullPath) || fullPath.Contains('"'))
                throw new InvalidOperationException("The application executable is unavailable.");
            ownedRegistration = $"Software\\Classes\\CLSID\\{{{id:D}}}\\LocalServer32";
            using var key = Registry.CurrentUser.CreateSubKey(ownedRegistration, writable: true);
            key.SetValue(null, $"\"{fullPath}\" {WindowsPasskeyIdentity.ActivationArgument}");
            key.SetValue("ServerExecutable", fullPath);
        }
        // Portable upgrades can move the executable; refresh the COM activation path even
        // when the authenticator registration and its platform signing key already exist.
        if (status.Registered)
            return;
        var info = WindowsPasskeyCtapResponse.AuthenticatorInfo();
        fixed (byte* infoPointer = info)
        fixed (char* name = WindowsPasskeyIdentity.DisplayName)
        fixed (char* rpId = WindowsPasskeyIdentity.PluginRpId)
        {
            var options = new AddOptions
            {
                Name = name, ClassId = &id, RpId = rpId, InfoLength = (uint)info.Length, Info = infoPointer,
            };
            nint response = 0;
            try
            {
                ThrowIfFailed(WebAuthNPluginAddAuthenticator(in options, out response));
            }
            catch
            {
                if (ownedRegistration is not null)
                    Registry.CurrentUser.DeleteSubKeyTree(ownedRegistration, throwOnMissingSubKey: false);
                throw;
            }
            finally
            {
                if (response != 0)
                    WebAuthNPluginFreeAddAuthenticatorResponse(response);
            }
        }
    }

    internal static void Unregister(bool packaged)
    {
        if (!GetStatus().Supported)
            return;
        var id = WindowsPasskeyIdentity.ClassId;
        var result = WebAuthNPluginRemoveAuthenticator(in id);
        if (result != NotFound) ThrowIfFailed(result);
        if (!packaged)
            Registry.CurrentUser.DeleteSubKeyTree($"Software\\Classes\\CLSID\\{{{id:D}}}", throwOnMissingSubKey: false);
    }

    internal static bool VerifyRequest(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        var id = WindowsPasskeyIdentity.ClassId;
        nint keyPointer = 0;
        try
        {
            if (WebAuthNPluginGetOperationSigningPublicKey(in id, out var length, out keyPointer) < 0
                || length is 0 or > 16384 || keyPointer == 0)
                return false;
            var keyBytes = new ReadOnlySpan<byte>((void*)keyPointer, checked((int)length));
            return VerifySignature(keyBytes, data, signature);
        }
        finally
        {
            if (keyPointer != 0) WebAuthNPluginFreePublicKeyResponse(keyPointer);
        }
    }

    internal static bool VerifySignature(ReadOnlySpan<byte> keyBytes, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (keyBytes.Length is < 8 or > 16384 || data.Length is 0 or > WindowsPasskeyIdentity.MaximumRequestBytes
            || signature.Length is 0 or > 16384)
            return false;
        try
        {
            using var key = CngKey.Import(keyBytes.ToArray(), CngKeyBlobFormat.GenericPublicBlob);
            if (key.AlgorithmGroup == CngAlgorithmGroup.Rsa)
            {
                using var rsa = new RSACng(key);
                return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            }
            if (key.AlgorithmGroup == CngAlgorithmGroup.ECDsa)
            {
                using var ec = new ECDsaCng(key);
                return ec.VerifyData(data, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            return false;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException) { return false; }
    }

    internal static WindowsPasskeyRequest Decode(
        PluginOperationRequest operation, WindowsPasskeyOperation kind, byte[] encoded)
    {
        fixed (byte* input = encoded)
        {
            nint decoded = 0;
            try
            {
                if (kind == WindowsPasskeyOperation.MakeCredential)
                {
                    ThrowIfFailed(WebAuthNDecodeMakeCredentialRequest((uint)encoded.Length, input, out decoded));
                    var value = Marshal.PtrToStructure<MakeRequestPrefix>(decoded);
                    ValidateOptionalFields(value.Options, value.ExtensionLength, value.Extensions, value.PinAuthLength);
                    if (value.Rp == 0 || value.User == 0) throw InvalidRequest();
                    var rp = Marshal.PtrToStructure<RpInformation>(value.Rp);
                    var user = Marshal.PtrToStructure<UserInformation>(value.User);
                    var algorithms = ReadAlgorithms(value.Parameters);
                    return new(operation.TransactionId, kind, ReadText(rp.Id, 253),
                        ReadBytes(value.Hash, value.HashLength, 32, 32), RequireUv(value.Options),
                        ReadCredentials(value.Credentials), ReadOptionalText(rp.Name),
                        ReadBytes(user.Id, user.IdLength, 1, 64), ReadOptionalText(user.Name),
                        ReadOptionalText(user.DisplayName), algorithms);
                }
                ThrowIfFailed(WebAuthNDecodeGetAssertionRequest((uint)encoded.Length, input, out decoded));
                var get = Marshal.PtrToStructure<GetRequestPrefix>(decoded);
                ValidateOptionalFields(get.Options, get.ExtensionLength, get.Extensions, get.PinAuthLength);
                return new(operation.TransactionId, kind, ReadText(get.RpId, 253),
                    ReadBytes(get.Hash, get.HashLength, 32, 32), RequireUv(get.Options), ReadCredentials(get.Credentials));
            }
            finally
            {
                if (decoded != 0)
                {
                    if (kind == WindowsPasskeyOperation.MakeCredential) WebAuthNFreeDecodedMakeCredentialRequest(decoded);
                    else WebAuthNFreeDecodedGetAssertionRequest(decoded);
                }
            }
        }
    }

    internal static void ReplaceCredentialMetadata(IReadOnlyList<WindowsPasskeyCredentialMetadata> metadata)
    {
        var id = WindowsPasskeyIdentity.ClassId;
        ThrowIfFailed(WebAuthNPluginAuthenticatorRemoveAllCredentials(in id));
        if (metadata.Count == 0) return;
        using var scope = new NativeScope();
        var values = new CredentialDetails[metadata.Count];
        for (var index = 0; index < values.Length; index++)
        {
            var item = metadata[index];
            values[index] = new CredentialDetails
            {
                CredentialIdLength = (uint)item.CredentialId.Length, CredentialId = scope.Bytes(item.CredentialId),
                RpId = scope.Text(item.RpId), RpName = scope.Text(item.RpName),
                UserIdLength = (uint)item.UserId.Length, UserId = scope.Bytes(item.UserId),
                UserName = scope.Text(item.UserName), UserDisplayName = scope.Text(item.UserDisplayName),
            };
        }
        fixed (CredentialDetails* details = values)
            ThrowIfFailed(WebAuthNPluginAuthenticatorAddCredentials(in id, (uint)values.Length, details));
    }

    internal static byte[] ReadBytes(nint pointer, uint length, int minimum, int maximum)
    {
        if (pointer == 0 || length < minimum || length > maximum) throw InvalidRequest();
        return new ReadOnlySpan<byte>((void*)pointer, checked((int)length)).ToArray();
    }

    private static string ReadText(nint pointer, int maximum = 512)
    {
        if (pointer == 0) throw InvalidRequest();
        var text = (char*)pointer;
        var length = 0;
        while (length <= maximum && text[length] != '\0') length++;
        if (length == 0 || length > maximum) throw InvalidRequest();
        return new string(text, 0, length);
    }

    private static string? ReadOptionalText(nint pointer) => pointer == 0 || *(char*)pointer == '\0' ? null : ReadText(pointer);
    private static bool RequireUv(nint options) => options != 0 && Marshal.PtrToStructure<AuthenticatorOptions>(options).Uv == 1;

    private static void ValidateOptionalFields(nint options, uint extensionLength, nint extensions, uint pinAuthLength)
    {
        if (pinAuthLength != 0 || extensionLength > 1
            || (extensionLength == 1 && (extensions == 0 || *(byte*)extensions != 0xA0)))
            throw new WindowsPasskeyException(WindowsPasskeyException.NotSupported, "Requested authenticator extensions are not supported.");
        if (options != 0 && Marshal.PtrToStructure<AuthenticatorOptions>(options).Up == -1)
            throw new WindowsPasskeyException(WindowsPasskeyException.NotSupported, "User presence is required.");
    }

    private static IReadOnlyList<int> ReadAlgorithms(CredentialParameters parameters)
    {
        if (parameters.Count is 0 or > 64 || parameters.Values == 0) throw InvalidRequest();
        var values = new List<int>();
        var stride = Marshal.SizeOf<CredentialParameter>();
        for (var index = 0; index < parameters.Count; index++)
        {
            var item = Marshal.PtrToStructure<CredentialParameter>(parameters.Values + index * stride);
            if (ReadText(item.Type, 16) == "public-key") values.Add(item.Algorithm);
        }
        return values;
    }

    private static IReadOnlyList<byte[]> ReadCredentials(CredentialList list)
    {
        if (list.Count > WindowsPasskeyIdentity.MaximumCredentialCount || (list.Count > 0 && list.Values == 0))
            throw InvalidRequest();
        var values = new List<byte[]>();
        for (var index = 0; index < list.Count; index++)
        {
            var pointer = Marshal.ReadIntPtr(list.Values, index * nint.Size);
            if (pointer == 0) throw InvalidRequest();
            var item = Marshal.PtrToStructure<Credential>(pointer);
            if (ReadText(item.Type, 16) != "public-key") continue;
            values.Add(ReadBytes(item.Id, item.IdLength, 1, WindowsPasskeyIdentity.MaximumCredentialIdBytes));
        }
        return values;
    }

    internal static void ThrowIfFailed(int result)
    {
        if (result < 0) throw new WindowsPasskeyException(result, $"The Windows passkey operation failed (0x{result:X8}).");
    }

    private static WindowsPasskeyException InvalidRequest() =>
        new(WindowsPasskeyException.InvalidRequest, "The Windows passkey request is invalid.");

    [StructLayout(LayoutKind.Sequential)] internal struct PluginOperationRequest
    {
        internal nint Window;
        internal Guid TransactionId;
        internal uint SignatureLength;
        internal nint Signature;
        internal uint RequestType;
        internal uint RequestLength;
        internal nint Request;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct PluginOperationResponse { internal uint Length; internal nint Data; }
    [StructLayout(LayoutKind.Sequential)] private struct AddOptions
    {
        internal char* Name; internal Guid* ClassId; internal char* RpId; internal nint LightLogo; internal nint DarkLogo;
        internal uint InfoLength; internal byte* Info; internal uint SupportedRpCount; internal nint SupportedRps;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CredentialParameters { internal uint Count; internal nint Values; }
    [StructLayout(LayoutKind.Sequential)] private struct CredentialParameter { internal uint Version; internal nint Type; internal int Algorithm; }
    [StructLayout(LayoutKind.Sequential)] private struct CredentialList { internal uint Count; internal nint Values; }
    [StructLayout(LayoutKind.Sequential)] private struct Credential { internal uint Version; internal uint IdLength; internal nint Id; internal nint Type; }
    [StructLayout(LayoutKind.Sequential)] private struct RpInformation { internal uint Version; internal nint Id; internal nint Name; internal nint Icon; }
    [StructLayout(LayoutKind.Sequential)] private struct UserInformation
    { internal uint Version; internal uint IdLength; internal nint Id; internal nint Name; internal nint Icon; internal nint DisplayName; }
    [StructLayout(LayoutKind.Sequential)] private struct AuthenticatorOptions { internal uint Version; internal int Up; internal int Uv; internal int Rk; }
    [StructLayout(LayoutKind.Sequential)] private struct MakeRequestPrefix
    {
        internal uint Version; internal uint RpIdLength; internal nint RpId; internal uint HashLength; internal nint Hash;
        internal nint Rp; internal nint User; internal CredentialParameters Parameters; internal CredentialList Credentials;
        internal uint ExtensionLength; internal nint Extensions; internal nint Options; internal int EmptyPinAuth;
        internal uint PinAuthLength; internal nint PinAuth;
    }
    [StructLayout(LayoutKind.Sequential)] private struct GetRequestPrefix
    {
        internal uint Version; internal nint RpId; internal uint RawRpIdLength; internal nint RawRpId;
        internal uint HashLength; internal nint Hash; internal CredentialList Credentials;
        internal uint ExtensionLength; internal nint Extensions; internal nint Options; internal int EmptyPinAuth;
        internal uint PinAuthLength; internal nint PinAuth;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CredentialDetails
    {
        internal uint CredentialIdLength; internal nint CredentialId; internal nint RpId; internal nint RpName;
        internal uint UserIdLength; internal nint UserId; internal nint UserName; internal nint UserDisplayName;
    }
    private sealed class NativeScope : IDisposable
    {
        private readonly List<nint> _allocations = [];
        internal nint Text(string? text)
        {
            if (text is null) return 0;
            var pointer = Marshal.StringToCoTaskMemUni(text); _allocations.Add(pointer); return pointer;
        }
        internal nint Bytes(byte[] data)
        {
            var pointer = Marshal.AllocCoTaskMem(data.Length); _allocations.Add(pointer);
            Marshal.Copy(data, 0, pointer, data.Length); return pointer;
        }
        public void Dispose() { foreach (var pointer in _allocations) Marshal.FreeCoTaskMem(pointer); }
    }

    [DllImport("WinVaultWarden.WebAuthnPlugin", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WebAuthNPluginGetAuthenticatorState(in Guid classId, out uint state);
    [DllImport("WinVaultWarden.WebAuthnPlugin", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WebAuthNPluginAddAuthenticator(in AddOptions options, out nint response);
    [DllImport("WinVaultWarden.WebAuthnPlugin", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void WebAuthNPluginFreeAddAuthenticatorResponse(nint response);
    [DllImport("WinVaultWarden.WebAuthnPlugin", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WebAuthNPluginRemoveAuthenticator(in Guid classId);
    [DllImport("WinVaultWarden.WebAuthnPlugin", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WebAuthNPluginGetOperationSigningPublicKey(in Guid classId, out uint length, out nint key);
    [DllImport("WinVaultWarden.WebAuthnPlugin", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void WebAuthNPluginFreePublicKeyResponse(nint response);
    [DllImport("WinVaultWarden.WebAuthnPlugin", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WebAuthNDecodeMakeCredentialRequest(uint length, byte* data, out nint request);
    [DllImport("WinVaultWarden.WebAuthnPlugin", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WebAuthNDecodeGetAssertionRequest(uint length, byte* data, out nint request);
    [DllImport("WinVaultWarden.WebAuthnPlugin", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void WebAuthNFreeDecodedMakeCredentialRequest(nint request);
    [DllImport("WinVaultWarden.WebAuthnPlugin", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void WebAuthNFreeDecodedGetAssertionRequest(nint request);
    [DllImport("WinVaultWarden.WebAuthnPlugin", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WebAuthNPluginAuthenticatorRemoveAllCredentials(in Guid classId);
    [DllImport("WinVaultWarden.WebAuthnPlugin", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WebAuthNPluginAuthenticatorAddCredentials(in Guid classId, uint count, CredentialDetails* details);
}
