using System.Collections.Concurrent;
using System.Formats.Cbor;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using App.Services;
using Core.Models;
using Core.Services;
using Core.Session;
using Core.WindowsPasskeys;
using Crypto;
using Vault;
using Xunit;

namespace WindowsPlatform.Tests;

/// <summary>
/// Uses only owned process memory, ephemeral keys and read-only OS state. Never starts
/// the COM host, registers a class, changes provider settings, opens UI or contacts an RP.
/// </summary>
public sealed class WindowsPasskeyNativeTests(ITestOutputHelper output)
{
    private static readonly Guid UnknownId = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid FactoryId = new("00000001-0000-0000-c000-000000000046");
    private static readonly Guid AuthenticatorId = new("d26bcf6f-b54c-43ff-9f06-d5bf148625f7");
    private const int NoInterface = unchecked((int)0x80004002);
    private const int NoAggregation = unchecked((int)0x80040110);
    private const int AccessDenied = unchecked((int)0x80070005);

    [WindowsFact]
    public void ReadOnlyCapabilityProbeReportsActualSystemExportsAndProviderState()
    {
        var supportedExports = new[]
        {
            "WebAuthNPluginAddAuthenticator", "WebAuthNPluginGetAuthenticatorState",
            "WebAuthNPluginGetOperationSigningPublicKey", "WebAuthNDecodeMakeCredentialRequest",
            "WebAuthNDecodeGetAssertionRequest", "WebAuthNPluginAuthenticatorAddCredentials",
        };
        foreach (var name in new[] { "webauthnplugin.dll", "webauthn.dll" })
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);
            if (!NativeLibrary.TryLoad(path, out var module))
            {
                output.WriteLine($"System module {name}: unavailable");
                continue;
            }
            try
            {
                foreach (var export in supportedExports)
                    output.WriteLine($"{name}!{export}: {NativeLibrary.TryGetExport(module, export, out _)}");
            }
            finally { NativeLibrary.Free(module); }
        }
        var state = WindowsPasskeyPlatformApi.GetStatus();
        output.WriteLine($"Read-only provider state: supported={state.Supported}, registered={state.Registered}, enabled={state.Enabled}");
        output.WriteLine($"Availability description: {state.Description}");
        Assert.False(state.Enabled && !state.Registered);
        Assert.False(state.Registered && !state.Supported);
        Assert.False(string.IsNullOrWhiteSpace(state.Description));
    }

    [WindowsFact]
    public void PluginStructuresMatchTheDocumented64BitWindowsAbi()
    {
        Assert.Equal(8, nint.Size);
        Assert.Equal(56, Marshal.SizeOf<WindowsPasskeyPlatformApi.PluginOperationRequest>());
        Assert.Equal(16, Marshal.SizeOf<WindowsPasskeyPlatformApi.PluginOperationResponse>());
        Assert.Equal(8, Marshal.OffsetOf<WindowsPasskeyPlatformApi.PluginOperationRequest>("TransactionId").ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<WindowsPasskeyPlatformApi.PluginOperationRequest>("SignatureLength").ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<WindowsPasskeyPlatformApi.PluginOperationRequest>("Signature").ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<WindowsPasskeyPlatformApi.PluginOperationRequest>("RequestType").ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<WindowsPasskeyPlatformApi.PluginOperationRequest>("Request").ToInt32());
        var expected = new Dictionary<string, int>
        {
            ["AddOptions"] = 72, ["CredentialParameters"] = 16, ["CredentialParameter"] = 24,
            ["CredentialList"] = 16, ["Credential"] = 24, ["RpInformation"] = 32,
            ["UserInformation"] = 40, ["AuthenticatorOptions"] = 16,
            ["MakeRequestPrefix"] = 120, ["GetRequestPrefix"] = 104, ["CredentialDetails"] = 64,
        };
        foreach (var pair in expected)
        {
            var type = typeof(WindowsPasskeyPlatformApi).GetNestedType(pair.Key, BindingFlags.NonPublic)!;
            Assert.NotNull(type);
            Assert.Equal(pair.Value, Marshal.SizeOf(type));
            output.WriteLine($"ABI {pair.Key}: {Marshal.SizeOf(type)} bytes");
        }
    }

    [WindowsFact]
    public unsafe void FactoryQueryInterfaceAndReferenceCountingUseRealUnmanagedCallbacks()
    {
        using var fixture = new Fixture();
        using var factory = NativeLease.Create(fixture.Provider, factory: true);
        Assert.Equal(2u, AddRef(factory.Pointer));
        Assert.Equal(1u, Release(factory.Pointer));
        var id = UnknownId;
        nint queried = 0;
        Assert.Equal(0, Query(factory.Pointer, &id, &queried));
        Assert.Equal(factory.Pointer, queried);
        Assert.Equal(1u, Release(queried));
        id = FactoryId;
        Assert.Equal(0, Query(factory.Pointer, &id, &queried));
        Assert.Equal(factory.Pointer, queried);
        Assert.Equal(1u, Release(queried));
        id = Guid.NewGuid();
        queried = (nint)123;
        Assert.Equal(NoInterface, Query(factory.Pointer, &id, &queried));
        Assert.Equal(0, queried);
        Assert.Equal(WindowsPasskeyException.InvalidRequest, Query(factory.Pointer, null, &queried));
    }

    [WindowsFact]
    public unsafe void FactoryCreatesAnOwnedAuthenticatorWithoutRegisteringAnyClass()
    {
        using var fixture = new Fixture();
        using var factory = NativeLease.Create(fixture.Provider, factory: true);
        var id = AuthenticatorId;
        nint pointer = 0;
        Assert.Equal(0, CreateInstance(factory.Pointer, 0, &id, &pointer));
        Assert.NotEqual(0, pointer);
        using var authenticator = new NativeLease(pointer);
        id = UnknownId;
        nint queried = 0;
        Assert.Equal(0, Query(pointer, &id, &queried));
        Assert.Equal(pointer, queried);
        Assert.Equal(1u, Release(queried));
        var locked = -1;
        Assert.Equal(0, LockStatus(pointer, &locked));
        Assert.Equal(0, locked);
        Assert.Equal(WindowsPasskeyException.InvalidRequest, LockStatus(pointer, null));
        Assert.Equal(0, fixture.Approval.Count);
    }

    [WindowsFact]
    public unsafe void FactoryRejectsAggregationAndUnknownInterfacesWithoutReturningAnObject()
    {
        using var fixture = new Fixture();
        using var factory = NativeLease.Create(fixture.Provider, factory: true);
        var id = AuthenticatorId;
        nint pointer = (nint)123;
        Assert.Equal(NoAggregation, CreateInstance(factory.Pointer, (nint)1, &id, &pointer));
        Assert.Equal(0, pointer);
        id = Guid.NewGuid();
        Assert.Equal(NoInterface, CreateInstance(factory.Pointer, 0, &id, &pointer));
        Assert.Equal(0, pointer);
    }

    [WindowsFact]
    public unsafe void NullAndStoppedRequestsFailBeforeAnyApprovalOrVaultWrite()
    {
        using var fixture = new Fixture();
        using var authenticator = NativeLease.Create(fixture.Provider, factory: false);
        var response = new WindowsPasskeyPlatformApi.PluginOperationResponse { Length = 123, Data = (nint)456 };
        Assert.Equal(WindowsPasskeyException.InvalidRequest, MakeCredential(authenticator.Pointer, 0, (nint)(&response)));
        Assert.Equal(WindowsPasskeyException.InvalidRequest, GetAssertion(authenticator.Pointer, 0, (nint)(&response)));
        var request = new WindowsPasskeyPlatformApi.PluginOperationRequest();
        Assert.Equal(WindowsPasskeyException.UserCancelled,
            MakeCredential(authenticator.Pointer, (nint)(&request), (nint)(&response)));
        Assert.Equal(0u, response.Length);
        Assert.Equal(0, response.Data);
        Assert.Equal(0, fixture.Approval.Count);
        Assert.Equal(0, fixture.Writer.Count);
    }

    [WindowsFact]
    public unsafe void ForgedPlatformRequestCannotReachApprovalOrWrite()
    {
        using var fixture = new Fixture();
        using var authenticator = NativeLease.Create(fixture.Provider, factory: false);
        fixture.SetAccepting(true);
        var encoded = new byte[] { 0xA0 };
        var signature = new byte[64];
        fixed (byte* encodedPointer = encoded)
        fixed (byte* signaturePointer = signature)
        {
            var request = new WindowsPasskeyPlatformApi.PluginOperationRequest
            {
                TransactionId = Guid.NewGuid(), RequestType = 1,
                Request = (nint)encodedPointer, RequestLength = (uint)encoded.Length,
                Signature = (nint)signaturePointer, SignatureLength = (uint)signature.Length,
            };
            var response = new WindowsPasskeyPlatformApi.PluginOperationResponse();
            var state = WindowsPasskeyPlatformApi.GetStatus();
            var result = GetAssertion(authenticator.Pointer, (nint)(&request), (nint)(&response));
            Assert.Equal(state.Enabled ? AccessDenied : WindowsPasskeyException.UserCancelled, result);
            output.WriteLine(state.Enabled
                ? "Forged request rejected by the real platform operation-signature gate."
                : "Forged request rejected by the unavailable/disabled platform gate; OS signing-key validation needs enabled-provider acceptance.");
            Assert.Equal(0u, response.Length);
            Assert.Equal(0, response.Data);
        }
        Assert.Equal(0, fixture.Approval.Count);
        Assert.Equal(0, fixture.Writer.Count);
    }

    [WindowsFact]
    public unsafe void CancellationOnlyAffectsTheExactOwnedTransaction()
    {
        using var fixture = new Fixture();
        using var authenticator = NativeLease.Create(fixture.Provider, factory: false);
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        fixture.Requests.TryAdd(id, first);
        fixture.Requests.TryAdd(otherId, second);
        Assert.Equal(WindowsPasskeyException.InvalidRequest, Cancel(authenticator.Pointer, 0));
        var unknown = Guid.NewGuid();
        Assert.Equal(WindowsPasskeyException.NotFound, Cancel(authenticator.Pointer, (nint)(&unknown)));
        Assert.Equal(0, Cancel(authenticator.Pointer, (nint)(&id)));
        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
        fixture.Requests.TryRemove(id, out _);
        fixture.Requests.TryRemove(otherId, out _);
        Assert.Equal(0, fixture.Approval.Count);
    }

    [WindowsFact]
    public void EphemeralRsaPssCngSignatureRoundTripsAndRejectsAlteredData()
    {
        using var rsa = new RSACng(2048);
        Assert.True(rsa.Key.IsEphemeral);
        var key = rsa.Key.Export(CngKeyBlobFormat.GenericPublicBlob);
        var data = new byte[] { 0xA1, 0x01, 0x02 };
        var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        Assert.True(WindowsPasskeyPlatformApi.VerifySignature(key, data, signature));
        data[2] ^= 1;
        Assert.False(WindowsPasskeyPlatformApi.VerifySignature(key, data, signature));
        output.WriteLine("Real Windows CNG RSA-PSS/SHA-256 verification used an ephemeral key.");
    }

    [WindowsFact]
    public void EphemeralEcdsaCngSignatureUsesTheWindowsFixedFieldFormat()
    {
        using var ec = new ECDsaCng(ECCurve.NamedCurves.nistP256);
        Assert.True(ec.Key.IsEphemeral);
        var key = ec.Key.Export(CngKeyBlobFormat.EccPublicBlob);
        var data = new byte[] { 0xA1, 0x01, 0x02 };
        var signature = ec.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Assert.Equal(64, signature.Length);
        Assert.True(WindowsPasskeyPlatformApi.VerifySignature(key, data, signature));
        signature[0] ^= 1;
        Assert.False(WindowsPasskeyPlatformApi.VerifySignature(key, data, signature));
        Assert.False(WindowsPasskeyPlatformApi.VerifySignature(new byte[8], data, signature));
        Assert.False(WindowsPasskeyPlatformApi.VerifySignature(key, [], signature));
        output.WriteLine("Real Windows CNG ECDSA-P256 verification used an ephemeral key and P1363 signatures.");
    }

    [WindowsPluginFact]
    public void RealWindowsDecoderMapsSyntheticGetAssertionWithoutTouchingProviderState()
    {
        var hash = SHA256.HashData("Synthetic native get request"u8);
        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(4);
        writer.WriteInt32(1); writer.WriteTextString("example.invalid");
        writer.WriteInt32(2); writer.WriteByteString(hash);
        writer.WriteInt32(3); WriteCredentialList(writer);
        writer.WriteInt32(5); WriteOptions(writer);
        writer.WriteEndMap();
        var transaction = Guid.NewGuid();
        var decoded = WindowsPasskeyPlatformApi.Decode(new WindowsPasskeyPlatformApi.PluginOperationRequest
        {
            TransactionId = transaction, RequestType = 1,
        }, WindowsPasskeyOperation.GetAssertion, writer.Encode());

        Assert.Equal(transaction, decoded.TransactionId);
        Assert.Equal("example.invalid", decoded.RpId);
        Assert.Equal(hash, decoded.ClientDataHash);
        Assert.True(decoded.RequireUserVerification);
        Assert.Equal(new byte[] { 4, 5, 6 }, Assert.Single(decoded.CredentialIds));
        output.WriteLine("The real Windows decoder returned expected RP, hash, options and credential list from owned CBOR memory.");
    }

    [WindowsPluginFact]
    public void RealWindowsDecoderMapsSyntheticMakeCredentialIncludingOptionalEmptyNames()
    {
        var hash = SHA256.HashData("Synthetic native create request"u8);
        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(6);
        writer.WriteInt32(1); writer.WriteByteString(hash);
        writer.WriteInt32(2); writer.WriteStartMap(2);
        writer.WriteTextString("id"); writer.WriteTextString("example.invalid");
        writer.WriteTextString("name"); writer.WriteTextString(string.Empty); writer.WriteEndMap();
        writer.WriteInt32(3); writer.WriteStartMap(3);
        writer.WriteTextString("id"); writer.WriteByteString(new byte[] { 1, 2, 3 });
        writer.WriteTextString("name"); writer.WriteTextString(string.Empty);
        writer.WriteTextString("displayName"); writer.WriteTextString(string.Empty); writer.WriteEndMap();
        writer.WriteInt32(4); writer.WriteStartArray(1); writer.WriteStartMap(2);
        writer.WriteTextString("alg"); writer.WriteInt32(-7);
        writer.WriteTextString("type"); writer.WriteTextString("public-key"); writer.WriteEndMap(); writer.WriteEndArray();
        writer.WriteInt32(5); WriteCredentialList(writer);
        writer.WriteInt32(7); WriteOptions(writer);
        writer.WriteEndMap();
        var decoded = WindowsPasskeyPlatformApi.Decode(new WindowsPasskeyPlatformApi.PluginOperationRequest
        {
            TransactionId = Guid.NewGuid(), RequestType = 1,
        }, WindowsPasskeyOperation.MakeCredential, writer.Encode());

        Assert.Equal("example.invalid", decoded.RpId);
        Assert.Equal(hash, decoded.ClientDataHash);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded.UserId);
        Assert.Null(decoded.RpName); Assert.Null(decoded.UserName); Assert.Null(decoded.UserDisplayName);
        Assert.Equal(-7, Assert.Single(decoded.Algorithms!));
        Assert.True(decoded.RequireUserVerification);
        Assert.Equal(new byte[] { 4, 5, 6 }, Assert.Single(decoded.CredentialIds));
    }

    [WindowsPluginFact]
    public void RealWindowsDecoderRejectsMalformedOwnedBuffer()
    {
        var exception = Assert.Throws<WindowsPasskeyException>(() => WindowsPasskeyPlatformApi.Decode(
            new WindowsPasskeyPlatformApi.PluginOperationRequest { TransactionId = Guid.NewGuid(), RequestType = 1 },
            WindowsPasskeyOperation.GetAssertion, new byte[] { 0xFF }));

        Assert.True(exception.ErrorCode < 0);
        output.WriteLine($"Malformed synthetic CBOR rejected with HRESULT 0x{exception.ErrorCode:X8}.");
    }

    private static void WriteCredentialList(CborWriter writer)
    {
        writer.WriteStartArray(1); writer.WriteStartMap(2);
        writer.WriteTextString("id"); writer.WriteByteString(new byte[] { 4, 5, 6 });
        writer.WriteTextString("type"); writer.WriteTextString("public-key");
        writer.WriteEndMap(); writer.WriteEndArray();
    }

    private static void WriteOptions(CborWriter writer)
    {
        writer.WriteStartMap(3);
        writer.WriteTextString("rk"); writer.WriteBoolean(true);
        writer.WriteTextString("up"); writer.WriteBoolean(true);
        writer.WriteTextString("uv"); writer.WriteBoolean(true);
        writer.WriteEndMap();
    }

    private static unsafe int Query(nint self, Guid* iid, nint* result) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)self)[0])(self, iid, result);
    private static unsafe uint AddRef(nint self) =>
        ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)self)[1])(self);
    private static unsafe uint Release(nint self) =>
        ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)self)[2])(self);
    private static unsafe int CreateInstance(nint self, nint outer, Guid* iid, nint* result) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)self)[3])(self, outer, iid, result);
    private static unsafe int MakeCredential(nint self, nint request, nint response) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)(*(nint**)self)[3])(self, request, response);
    private static unsafe int GetAssertion(nint self, nint request, nint response) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)(*(nint**)self)[4])(self, request, response);
    private static unsafe int Cancel(nint self, nint request) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)self)[5])(self, request);
    private static unsafe int LockStatus(nint self, int* status) =>
        ((delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)self)[6])(self, status);

    private sealed class NativeLease(nint pointer) : IDisposable
    {
        private bool _disposed;
        internal nint Pointer { get; } = pointer;
        internal static NativeLease Create(WindowsPasskeyProviderService owner, bool factory)
        {
            var type = typeof(WindowsPasskeyProviderService).GetNestedType("NativeComObject", BindingFlags.NonPublic)!;
            var instance = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null, args: [owner, factory], culture: null)!;
            var nativePointer = (nint)type.GetProperty("Pointer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
            return new NativeLease(nativePointer);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Assert.Equal(0u, Release(Pointer));
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly VaultSession Session = new();
        internal readonly Approval Approval = new();
        internal readonly Writer Writer = new();
        internal WindowsPasskeyProviderService Provider { get; }
        internal ConcurrentDictionary<Guid, CancellationTokenSource> Requests =>
            (ConcurrentDictionary<Guid, CancellationTokenSource>)typeof(WindowsPasskeyProviderService)
                .GetField("_requests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Provider)!;
        internal Fixture()
        {
            Session.BeginAccountSession("synthetic-native-tests", "synthetic@example.invalid", new ServerOrigin("https://synthetic.invalid"));
            Session.SetUnlockedKey(new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64)));
            Provider = new WindowsPasskeyProviderService(new WindowsPasskeyAuthenticator(new VaultService(Session), Writer, Approval));
        }
        internal void SetAccepting(bool value) => typeof(WindowsPasskeyProviderService)
            .GetField("_accepting", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Provider, value);
        public void Dispose()
        {
            SetAccepting(false);
            Provider.StopAsync().GetAwaiter().GetResult();
            Session.Logout();
        }
    }

    private sealed class Approval : IWindowsPasskeyApprovalService
    {
        internal int Count;
        public Task<bool> EnsureUnlockedAsync(CancellationToken ct) { Count++; return Task.FromResult(false); }
        public Task<WindowsPasskeyApproval> ConfirmCreateAsync(WindowsPasskeyApprovalRequest request, CancellationToken ct)
        { Count++; return Task.FromResult(new WindowsPasskeyApproval(new(false, false, false))); }
        public Task<WindowsPasskeyApproval> ConfirmUseAsync(WindowsPasskeyApprovalRequest request, CancellationToken ct)
        { Count++; return Task.FromResult(new WindowsPasskeyApproval(new(false, false, false))); }
    }

    private sealed class Writer : IVaultWriteService
    {
        internal int Count;
        public Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default) { Count++; throw new InvalidOperationException("Unexpected synthetic vault write."); }
        public Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RestoreCipherAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveFolderAsync(string? id, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteFolderAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
