using System.Formats.Cbor;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Core.Enums;
using Core.Models;
using Core.Passkeys;
using Core.Services;
using Core.Session;
using Core.WindowsPasskeys;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class WindowsPasskeyAuthenticatorTests
{
    [Fact]
    public async Task AssertionSignsProvidedClientDataHashWithoutHashingItAgain()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var cipher = Existing(key);
        var session = Session(cipher);
        var approval = new Approval();
        var service = new WindowsPasskeyAuthenticator(new VaultService(session), new Writer(), approval);
        var request = GetRequest();
        var encoded = await service.HandleAsync(request, TestContext.Current.CancellationToken);
        var reader = new CborReader(encoded, CborConformanceMode.Ctap2Canonical);
        Assert.Equal(4, reader.ReadStartMap());
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal(2, reader.ReadStartMap());
        Assert.Equal("id", reader.ReadTextString());
        Assert.Equal(CredentialId.DecodeStorage(cipher.Login!.Fido2Credentials[0].CredentialId!), reader.ReadByteString());
        Assert.Equal("type", reader.ReadTextString()); Assert.Equal("public-key", reader.ReadTextString()); reader.ReadEndMap();
        Assert.Equal(2, reader.ReadInt32()); var authData = reader.ReadByteString();
        Assert.Equal(3, reader.ReadInt32()); var signature = reader.ReadByteString();
        Assert.Equal(4, reader.ReadInt32()); reader.ReadStartMap(); reader.ReadTextString();
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadByteString()); reader.ReadEndMap(); reader.ReadEndMap();
        Assert.Equal(0x1d, authData[32]);
        var signed = authData.Concat(request.ClientDataHash).ToArray();
        Assert.True(key.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Assert.Equal("example.com", approval.LastRequest!.RpId);
    }

    [Fact]
    public async Task CreateSavesCredentialBeforeReturningCtapAttestation()
    {
        var writer = new Writer();
        var service = new WindowsPasskeyAuthenticator(new VaultService(Session()), writer, new Approval());
        var request = GetRequest() with
        {
            Operation = WindowsPasskeyOperation.MakeCredential,
            UserId = [1, 2, 3], UserName = "account", RpName = "Example", Algorithms = [-7],
        };
        var encoded = await service.HandleAsync(request, TestContext.Current.CancellationToken);
        var reader = new CborReader(encoded, CborConformanceMode.Ctap2Canonical);
        Assert.Equal(3, reader.ReadStartMap());
        Assert.Equal(1, reader.ReadInt32()); Assert.Equal("none", reader.ReadTextString());
        Assert.Equal(2, reader.ReadInt32()); var authData = reader.ReadByteString();
        var credential = Assert.Single(writer.Saved!.Login!.Fido2Credentials);
        Assert.True(Guid.TryParseExact(credential.CredentialId, "D", out _));
        Assert.Equal("AQID", credential.UserHandle);
        Assert.Equal("example.com", credential.RpId);
        Assert.Equal(0x5d, authData[32]);
        var id = CredentialId.DecodeStorage(credential.CredentialId!);
        Assert.Equal(id, authData.AsSpan(55, id.Length).ToArray());
        var publicKey = new CborReader(authData.AsMemory(55 + id.Length), CborConformanceMode.Ctap2Canonical);
        publicKey.ReadStartMap(); publicKey.ReadInt32(); publicKey.ReadInt32();
        publicKey.ReadInt32(); publicKey.ReadInt32(); publicKey.ReadInt32(); publicKey.ReadInt32();
        Assert.Equal(-2, publicKey.ReadInt32()); var x = publicKey.ReadByteString();
        Assert.Equal(-3, publicKey.ReadInt32()); var y = publicKey.ReadByteString();
        using var privateKey = ECDsa.Create(); privateKey.ImportPkcs8PrivateKey(WebAuthnCrypto.DecodeBase64Url(credential.KeyValue!), out _);
        Assert.Equal(x, privateKey.ExportParameters(false).Q.X);
        Assert.Equal(y, privateKey.ExportParameters(false).Q.Y);
    }

    [Fact]
    public async Task ExcludedCredentialPreventsCreationBeforeApproval()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var cipher = Existing(key);
        var approval = new Approval();
        var service = new WindowsPasskeyAuthenticator(new VaultService(Session(cipher)), new Writer(), approval);
        var request = GetRequest() with
        {
            Operation = WindowsPasskeyOperation.MakeCredential, UserId = [1], Algorithms = [-7],
            CredentialIds = [CredentialId.DecodeStorage(cipher.Login!.Fido2Credentials[0].CredentialId!)],
        };
        var exception = await Assert.ThrowsAsync<WindowsPasskeyException>(() => service.HandleAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(WindowsPasskeyException.AlreadyExists, exception.ErrorCode);
        Assert.Null(approval.LastRequest);
    }

    [Fact]
    public async Task LockDuringApprovalPreventsSignature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var session = Session(Existing(key));
        var approval = new Approval { Wait = true };
        var service = new WindowsPasskeyAuthenticator(new VaultService(session), new Writer(), approval);
        var running = service.HandleAsync(GetRequest(), TestContext.Current.CancellationToken);
        await approval.Entered.Task;
        session.Lock();
        approval.Continue.TrySetResult();
        var exception = await Assert.ThrowsAsync<WindowsPasskeyException>(() => running);
        Assert.Equal(WindowsPasskeyException.UserCancelled, exception.ErrorCode);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("archived")]
    [InlineData("permission")]
    [InlineData("key")]
    public async Task SnapshotRevocationDuringApprovalPreventsUsingOldPrivateKey(string change)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var replacementKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = Existing(key);
        var session = Session(original);
        var approval = new Approval { Wait = true };
        var service = new WindowsPasskeyAuthenticator(new VaultService(session), new Writer(), approval);
        var running = service.HandleAsync(GetRequest(), TestContext.Current.CancellationToken);
        await approval.Entered.Task;
        var updated = new Cipher
        {
            Id = original.Id, Type = original.Type, Name = original.Name,
            Login = change == "key" ? Existing(replacementKey).Login : original.Login,
            DeletedDate = change == "deleted" ? DateTimeOffset.UtcNow : null,
            ArchivedDate = change == "archived" ? DateTimeOffset.UtcNow : null,
            CanViewPassword = change != "permission",
        };
        ReplaceSnapshot(session, updated);
        approval.Continue.TrySetResult();
        var exception = await Assert.ThrowsAsync<WindowsPasskeyException>(() => running);
        Assert.Equal(WindowsPasskeyException.UserCancelled, exception.ErrorCode);
    }

    [Fact]
    public async Task NonzeroCounterIsPersistedBeforeItIsReturned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var writer = new Writer();
        var session = Session(Existing(key, counter: 7));
        var sync = new Sync(session);
        var service = new WindowsPasskeyAuthenticator(new VaultService(session), writer, new Approval(), sync);
        var encoded = await service.HandleAsync(GetRequest(), TestContext.Current.CancellationToken);
        var reader = new CborReader(encoded, CborConformanceMode.Ctap2Canonical);
        reader.ReadStartMap(); reader.ReadInt32(); reader.SkipValue();
        Assert.Equal(2, reader.ReadInt32());
        var authData = reader.ReadByteString();
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32BigEndian(authData.AsSpan(33, 4)));
        Assert.Equal(8, Assert.Single(writer.Saved!.Login!.Fido2Credentials).Counter);
        Assert.Equal(1, sync.Count);
    }

    [Fact]
    public async Task PositiveCounterIsRefreshedFromServerBeforeApprovalAndIncrement()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var session = Session(Existing(key, counter: 7));
        var sync = new Sync(session) { Before = () => ReplaceSnapshot(session, Existing(key, counter: 12)) };
        var writer = new Writer();
        var service = new WindowsPasskeyAuthenticator(new VaultService(session), writer, new Approval(), sync);

        await service.HandleAsync(GetRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(13, Assert.Single(writer.Saved!.Login!.Fido2Credentials).Counter);
        Assert.Equal(1, sync.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NetworkShapedCancellationIsNotConvertedToUserCancellation(bool hasTimeoutReason)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var session = Session(Existing(key, counter: 1));
        var failure = new TaskCanceledException("Network request stopped",
            hasTimeoutReason ? new TimeoutException("Network deadline") : null);
        var sync = new Sync(session) { Failure = failure };
        var service = new WindowsPasskeyAuthenticator(new VaultService(session), new Writer(), new Approval(), sync);

        var actual = await Assert.ThrowsAsync<TaskCanceledException>(() => service.HandleAsync(
            GetRequest(), TestContext.Current.CancellationToken));

        Assert.Same(failure, actual);
        Assert.Equal(hasTimeoutReason ? WindowsPasskeyFailurePolicy.RequestTimeout : WindowsPasskeyFailurePolicy.UnexpectedFailure,
            WindowsPasskeyFailurePolicy.ToHResult(actual));
    }

    [Fact]
    public async Task OwnedRequestCancellationDuringApprovalProducesExplicitProtocolCancellation()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var requestCancellation = new CancellationTokenSource();
        var approval = new Approval { Wait = true };
        var service = new WindowsPasskeyAuthenticator(new VaultService(Session(Existing(key))), new Writer(), approval);
        var running = service.HandleAsync(GetRequest(), requestCancellation.Token);
        await approval.Entered.Task;

        requestCancellation.Cancel();
        approval.Continue.TrySetResult();

        var exception = await Assert.ThrowsAsync<WindowsPasskeyException>(() => running);
        Assert.Equal(WindowsPasskeyException.UserCancelled, exception.ErrorCode);
    }

    [Fact]
    public async Task LockedOperationUsesNormalUnlockAndRefusesIncompleteUnlock()
    {
        var session = Session(); session.Lock();
        var approval = new Approval { Unlock = false };
        var service = new WindowsPasskeyAuthenticator(new VaultService(session), new Writer(), approval);
        var exception = await Assert.ThrowsAsync<WindowsPasskeyException>(() => service.HandleAsync(GetRequest(), TestContext.Current.CancellationToken));
        Assert.Equal(WindowsPasskeyException.UserCancelled, exception.ErrorCode);
        Assert.True(approval.UnlockRequested);
    }

    [Fact]
    public async Task ForgedSelectionIsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var approval = new Approval { Forged = true };
        var service = new WindowsPasskeyAuthenticator(new VaultService(Session(Existing(key))), new Writer(), approval);
        var exception = await Assert.ThrowsAsync<WindowsPasskeyException>(() => service.HandleAsync(GetRequest(), TestContext.Current.CancellationToken));
        Assert.Equal(WindowsPasskeyException.UserCancelled, exception.ErrorCode);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task RequiredEvidenceMustBeActuallyPresent(bool approved, bool present, bool verified)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var service = new WindowsPasskeyAuthenticator(new VaultService(Session(Existing(key))), new Writer(),
            new Approval { Evidence = new(approved, present, verified) });
        var exception = await Assert.ThrowsAsync<WindowsPasskeyException>(() => service.HandleAsync(GetRequest(), TestContext.Current.CancellationToken));
        Assert.Equal(WindowsPasskeyException.UserCancelled, exception.ErrorCode);
    }

    [Theory]
    [InlineData("other.example")]
    [InlineData("com")]
    public async Task ExactCredentialRpMustMatch(string rpId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var approval = new Approval();
        var service = new WindowsPasskeyAuthenticator(new VaultService(Session(Existing(key))), new Writer(), approval);
        var exception = await Assert.ThrowsAsync<WindowsPasskeyException>(() => service.HandleAsync(GetRequest() with { RpId = rpId }, TestContext.Current.CancellationToken));
        Assert.Equal(WindowsPasskeyException.NotFound, exception.ErrorCode);
        Assert.Null(approval.LastRequest);
    }

    [Fact]
    public async Task MalformedClientDataHashIsRejected()
    {
        var service = new WindowsPasskeyAuthenticator(new VaultService(Session()), new Writer(), new Approval());
        var exception = await Assert.ThrowsAsync<WindowsPasskeyException>(() => service.HandleAsync(GetRequest() with { ClientDataHash = [1] }, TestContext.Current.CancellationToken));
        Assert.Equal(WindowsPasskeyException.InvalidRequest, exception.ErrorCode);
    }

    [Fact]
    public void MetadataOnlyContainsUsablePublicFieldsAndDisappearsWhenLocked()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var session = Session(Existing(key));
        var service = new WindowsPasskeyAuthenticator(new VaultService(session), new Writer(), new Approval());
        var item = Assert.Single(service.GetCredentialMetadata());
        Assert.Equal("example.com", item.RpId);
        Assert.Equal(new byte[] { 1, 2, 3 }, item.UserId);
        session.Lock();
        Assert.Empty(service.GetCredentialMetadata());
    }

    [Fact]
    public void InfoOnlyAdvertisesImplementedExtensionsAndAlgorithm()
    {
        var reader = new CborReader(WindowsPasskeyCtapResponse.AuthenticatorInfo(), CborConformanceMode.Ctap2Canonical);
        Assert.Equal(5, reader.ReadStartMap());
        var keys = new List<int>();
        while (reader.PeekState() != CborReaderState.EndMap) { keys.Add(reader.ReadInt32()); reader.SkipValue(); }
        Assert.Equal(new[] { 1, 3, 4, 9, 10 }, keys);
        reader.ReadEndMap();
    }

    private static WindowsPasskeyRequest GetRequest() => new(Guid.NewGuid(), WindowsPasskeyOperation.GetAssertion,
        "example.com", SHA256.HashData("client data"u8), true, []);

    private static Cipher Existing(ECDsa key, long counter = 0) => new()
    {
        Id = "cipher", Type = CipherType.Login, Name = "Example",
        Login = new CipherLogin("account", null, null, [])
        {
            Fido2Credentials = [new CipherFido2Credential("edb1bd31-c6fd-4704-8374-8d5be279fd63",
                "public-key", "ECDSA", "P-256", WebAuthnCrypto.EncodeBase64Url(key.ExportPkcs8PrivateKey()),
                "example.com", "AQID", "account", counter, "Example", "Account", true, DateTimeOffset.UtcNow)],
        },
    };

    private static VaultSession Session(params Cipher[] ciphers)
    {
        var session = new VaultSession();
        var origin = new ServerOrigin("https://vault.example");
        var lease = session.BeginAccountSession("account", "account@example.com", origin);
        session.TryCommit(lease, () =>
        {
            session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
            session.SetSnapshot(new DecryptedVault(new AccountInfo("account", "account@example.com", origin, "Account", ""), [], ciphers, 0, 0));
        });
        return session;
    }

    private static void ReplaceSnapshot(VaultSession session, params Cipher[] ciphers)
    {
        var lease = session.CaptureLease();
        session.TryCommit(lease, () => session.SetSnapshot(new DecryptedVault(
            new AccountInfo("account", "account@example.com", new ServerOrigin("https://vault.example"), "Account", ""),
            [], ciphers, 0, 0)));
    }

    private sealed class Approval : IWindowsPasskeyApprovalService
    {
        internal WindowsPasskeyApprovalRequest? LastRequest;
        internal bool Wait;
        internal bool Forged;
        internal bool Unlock = true;
        internal bool UnlockRequested;
        internal PasskeyApprovalEvidence Evidence = new(true, true, true);
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> EnsureUnlockedAsync(CancellationToken ct) { UnlockRequested = true; return Task.FromResult(Unlock); }
        public Task<WindowsPasskeyApproval> ConfirmCreateAsync(WindowsPasskeyApprovalRequest request, CancellationToken ct)
        { LastRequest = request; return Task.FromResult(new WindowsPasskeyApproval(Evidence)); }
        public async Task<WindowsPasskeyApproval> ConfirmUseAsync(WindowsPasskeyApprovalRequest request, CancellationToken ct)
        {
            LastRequest = request; Entered.TrySetResult();
            if (Wait) await Continue.Task;
            var candidate = request.Candidates[0];
            return new WindowsPasskeyApproval(Evidence, Forged ? "forged" : candidate.CipherId, candidate.CredentialId);
        }
    }

    private sealed class Writer : IVaultWriteService
    {
        internal Cipher? Saved;
        public Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default) { Saved = cipher; return Task.CompletedTask; }
        public Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RestoreCipherAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveFolderAsync(string? id, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteFolderAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Sync(VaultSession session) : ISyncService
    {
        internal int Count;
        internal Action? Before;
        internal Exception? Failure;
        public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default) =>
            SyncAsync(session.CaptureLease(), ct);
        public Task<IReadOnlyList<Cipher>> SyncAsync(VaultSessionLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); session.ThrowIfExpired(lease);
            if (Failure is not null) return Task.FromException<IReadOnlyList<Cipher>>(Failure);
            Count++; Before?.Invoke();
            return Task.FromResult(session.GetCiphers(lease));
        }
    }
}
