using System.Security.Cryptography;
using System.Text;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class AuthServicePersistenceFailureTests
{
    private const string Email = "me@example.com";
    private const string Password = "correct horse battery staple";
    private const string Pin = "1234";
    private const string Server = "https://vault.example";
    private const int Iterations = 600_000;

    [Fact]
    public async Task UnlockAsync_WhenSessionSaveThrowsIOException_ReturnsFailureAndStaysLocked()
    {
        await AssertExpectedSaveFailureAsync(
            LocalUnlockKind.Password,
            new IOException("disk full"));
    }

    [Fact]
    public async Task UnlockWithPinAsync_WhenSessionSaveThrowsUnauthorizedAccess_ReturnsFailureAndStaysLocked()
    {
        await AssertExpectedSaveFailureAsync(
            LocalUnlockKind.Pin,
            new UnauthorizedAccessException("session directory is read-only"));
    }

    [Fact]
    public async Task UnlockWithHelloAsync_WhenSessionSaveThrowsCryptographicException_ReturnsFailureAndStaysLocked()
    {
        await AssertExpectedSaveFailureAsync(
            LocalUnlockKind.Hello,
            new CryptographicException("DPAPI protect failed"));
    }

    [Fact]
    public async Task UnlockWithKeyConnectorAsync_WhenSessionSaveThrowsIOException_ReturnsSafeFailure()
    {
        await AssertKeyConnectorSaveFailureAsync(new IOException("disk full"));
    }

    [Fact]
    public async Task UnlockWithKeyConnectorAsync_WhenSessionSaveThrowsUnauthorizedAccess_ReturnsSafeFailure()
    {
        await AssertKeyConnectorSaveFailureAsync(
            new UnauthorizedAccessException("session directory is read-only"));
    }

    [Fact]
    public async Task UnlockWithKeyConnectorAsync_WhenSessionSaveThrowsCryptographicException_ReturnsSafeFailure()
    {
        await AssertKeyConnectorSaveFailureAsync(
            new CryptographicException("DPAPI protect failed"));
    }

    [Fact]
    public async Task UnlockWithKeyConnectorAsync_WhenSessionSaveThrowsInvalidDataException_ReturnsSafeFailure()
    {
        await AssertKeyConnectorSaveFailureAsync(
            new InvalidDataException("session payload is invalid"));
    }

    [Theory]
    [InlineData(PersistenceFailureKind.IOException)]
    [InlineData(PersistenceFailureKind.UnauthorizedAccess)]
    [InlineData(PersistenceFailureKind.Cryptographic)]
    [InlineData(PersistenceFailureKind.InvalidData)]
    public async Task LoginAsync_WhenSessionSaveFailsRollsBackAndReturnsSafeFailure(
        PersistenceFailureKind failureKind)
    {
        var crypto = new CapturingCryptoService();
        var persisted = CreatePersistedSession(crypto, out var helloSignature);
        var tokenStore = new ThrowingSaveTokenStore(
            persisted,
            CreatePersistenceFailure(failureKind));
        var identity = new ControlledIdentityApiClient(_ =>
            Task.FromResult<ConnectTokenResult>(new ConnectTokenResult.Success(
                new TokenResponse(
                    TestAccessToken(),
                    "fresh-refresh",
                    3600,
                    "Bearer",
                    "api offline_access",
                    persisted.ProtectedUserKey,
                    null,
                    KdfType.Pbkdf2,
                    Iterations))));
        var session = new VaultSession();
        var service = CreateAuthService(
            crypto,
            session,
            tokenStore,
            identity: identity);

        try
        {
            var result = await service.LoginAsync(
                Server,
                Email,
                Password,
                TestContext.Current.CancellationToken);

            var failure = Assert.IsType<AuthResult.Failure>(result);
            Assert.Equal(AuthFailureKind.SafeMessage, failure.Kind);
            Assert.Equal(
                "The authenticated session could not be stored securely.",
                failure.Message);
            Assert.Equal(VaultState.LoggedOut, session.State);
            Assert.Null(session.UserKey);
            Assert.Null(session.AccessToken);
            Assert.Null(session.RefreshToken);
            AssertDecryptedKeyWasDisposed(crypto);
            Assert.Equal(1, tokenStore.SaveCount);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(helloSignature);
        }
    }

    [Theory]
    [InlineData(LogoutRemovalFailureKind.ReturnedFalse)]
    [InlineData(LogoutRemovalFailureKind.IOException)]
    [InlineData(LogoutRemovalFailureKind.UnauthorizedAccess)]
    [InlineData(LogoutRemovalFailureKind.Cryptographic)]
    [InlineData(LogoutRemovalFailureKind.InvalidData)]
    public async Task LogoutAsync_WhenPersistedSessionRemovalFailsDoesNotReportSuccess(
        LogoutRemovalFailureKind failureKind)
    {
        const string sensitiveDetail = "SENSITIVE-STORAGE-PATH";
        var crypto = new CryptoService();
        var persisted = CreatePersistedSession(crypto, out var helloSignature);
        var tokenStore = new FailingRemoveTokenStore(
            persisted,
            failureKind,
            sensitiveDetail);
        var session = new VaultSession();
        session.RestorePersistedSession(persisted);
        var userKeyBytes = Enumerable.Repeat((byte)0x5A, 64).ToArray();
        session.SetTokens("access-token", "refresh-token");
        session.SetUnlockedKey(new SymmetricCryptoKey(userKeyBytes));
        var service = CreateAuthService(crypto, session, tokenStore);

        try
        {
            var exception = await Assert.ThrowsAsync<AuthSessionPersistenceException>(
                () => service.LogoutAsync(TestContext.Current.CancellationToken));

            Assert.Equal(
                "The saved session could not be removed securely. Retry logout after checking storage access.",
                exception.Message);
            Assert.DoesNotContain(sensitiveDetail, exception.ToString(), StringComparison.Ordinal);
            Assert.Equal(VaultState.Locked, session.State);
            Assert.Equal(persisted.AccountId, session.Account.Id);
            Assert.Null(session.UserKey);
            Assert.Null(session.AccessToken);
            Assert.Null(session.RefreshToken);
            Assert.All(userKeyBytes, value => Assert.Equal(0, value));
            Assert.Equal(1, tokenStore.RemoveCount);

            if (failureKind == LogoutRemovalFailureKind.ReturnedFalse)
            {
                tokenStore.AllowRemoval = true;
                await service.LogoutAsync(TestContext.Current.CancellationToken);
                Assert.Equal(VaultState.LoggedOut, session.State);
                Assert.Equal(2, tokenStore.RemoveCount);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(helloSignature);
        }
    }

    [Fact]
    public async Task UnlockAsync_WhenSessionSaveThrowsUnexpectedException_PropagatesAndDisposesKey()
    {
        var crypto = new CapturingCryptoService();
        var persisted = CreatePersistedSession(crypto, out var helloSignature);
        var tokenStore = new ThrowingSaveTokenStore(
            persisted,
            new InvalidOperationException("unexpected persistence failure"));
        var session = new VaultSession();
        var service = CreateAuthService(crypto, session, tokenStore);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UnlockAsync(Password, TestContext.Current.CancellationToken));

            Assert.Equal("unexpected persistence failure", exception.Message);
            AssertLockedWithoutSecrets(session);
            AssertDecryptedKeyWasDisposed(crypto);
            Assert.Equal(1, tokenStore.SaveCount);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(helloSignature);
        }
    }

    private static async Task AssertExpectedSaveFailureAsync(
        LocalUnlockKind unlockKind,
        Exception saveFailure)
    {
        var crypto = new CapturingCryptoService();
        var persisted = CreatePersistedSession(crypto, out var helloSignature);
        var tokenStore = new ThrowingSaveTokenStore(persisted, saveFailure);
        var session = new VaultSession();
        var service = CreateAuthService(crypto, session, tokenStore);
        byte[]? helloInput = null;
        try
        {
            AuthResult result;
            switch (unlockKind)
            {
                case LocalUnlockKind.Password:
                    result = await service.UnlockAsync(
                        Password,
                        TestContext.Current.CancellationToken);
                    break;
                case LocalUnlockKind.Pin:
                    result = await service.UnlockWithPinAsync(
                        Pin,
                        TestContext.Current.CancellationToken);
                    break;
                case LocalUnlockKind.Hello:
                    helloInput = helloSignature.ToArray();
                    result = await service.UnlockWithHelloAsync(
                        helloInput,
                        TestContext.Current.CancellationToken);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(unlockKind));
            }

            var failure = Assert.IsType<AuthResult.Failure>(result);
            Assert.Equal("无法安全保存会话，请检查磁盘权限后重试。", failure.Message);
            AssertLockedWithoutSecrets(session);
            AssertDecryptedKeyWasDisposed(crypto);
            Assert.Equal(1, tokenStore.SaveCount);
            if (helloInput is not null)
                Assert.All(helloInput, value => Assert.Equal(0, value));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(helloSignature);
        }
    }

    private static async Task AssertKeyConnectorSaveFailureAsync(Exception saveFailure)
    {
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            ServerEndpoints.FromWebVault(Server),
            1,
            Email,
            "device-id",
            "stored-refresh",
            "2.invalid",
            KdfType.Pbkdf2,
            Iterations,
            null,
            null,
            unlockMethod: PersistedSessionUnlockMethod.KeyConnector,
            keyConnectorUrl: "https://keys.example");
        var tokenStore = new ThrowingSaveTokenStore(persisted, saveFailure);
        var session = new VaultSession();
        var keyConnector = new TrackingKeyConnectorClient();
        var service = CreateAuthService(
            new CryptoService(),
            session,
            tokenStore,
            keyConnector);

        var result = await service.UnlockWithKeyConnectorAsync(
            service.BeginAuthenticationAttempt(),
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.SafeMessage, failure.Kind);
        Assert.Equal(
            "The authenticated session could not be stored securely.",
            failure.Message);
        AssertLockedWithoutSecrets(session);
        Assert.True(session.CaptureLease().Generation > persisted.Generation);
        Assert.Equal(1, tokenStore.SaveCount);
        Assert.Equal(0, keyConnector.CallCount);
    }

    private static AuthService CreateAuthService(
        CryptoService crypto,
        VaultSession session,
        ITokenStore tokenStore,
        IKeyConnectorClient? keyConnector = null,
        IIdentityApiClient? identity = null)
    {
        var cache = new VaultCacheService(
            new FakeCacheStore(),
            new VaultDecryptor(crypto),
            session);
        var clients = identity is null
            ? new FakeVaultApiClientProvider()
            : FakeVaultApiClientProvider.ForAnonymous<IIdentityApiClient>(identity);
        return new AuthService(
            clients,
            crypto,
            session,
            tokenStore,
            bootstrapper: null!,
            cache,
            refresher: null!,
            keyConnector);
    }

    private static string TestAccessToken()
    {
        static string Encode(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        return $"{Encode("{\"alg\":\"none\"}")}.{Encode($"{{\"sub\":\"{Email}\"}}")}.signature";
    }

    private static Exception CreatePersistenceFailure(PersistenceFailureKind kind) => kind switch
    {
        PersistenceFailureKind.IOException => new IOException("disk full"),
        PersistenceFailureKind.UnauthorizedAccess =>
            new UnauthorizedAccessException("session directory is read-only"),
        PersistenceFailureKind.Cryptographic =>
            new CryptographicException("DPAPI protect failed"),
        PersistenceFailureKind.InvalidData =>
            new InvalidDataException("session payload is invalid"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static PersistedSession CreatePersistedSession(
        CryptoService crypto,
        out byte[] helloSignature)
    {
        using var userKey = new SymmetricCryptoKey(
            Enumerable.Range(64, 64).Select(value => (byte)value).ToArray());

        var masterKey = crypto.DeriveMasterKey(
            Password,
            Email,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null);
        string protectedUserKey;
        try
        {
            using var stretchedKey = crypto.StretchMasterKey(masterKey);
            protectedUserKey = crypto.Encrypt(userKey.FullKey, stretchedKey).ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }

        var pinMasterKey = crypto.DeriveMasterKey(
            Pin,
            Email,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null);
        string pinProtectedUserKey;
        try
        {
            pinProtectedUserKey = crypto.ProtectUserKey(pinMasterKey, userKey).ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinMasterKey);
        }

        helloSignature = Enumerable.Repeat((byte)5, 256).ToArray();
        string helloProtectedUserKey;
        using (var helloKey = crypto.DeriveKeyFromSecret(helloSignature))
            helloProtectedUserKey = crypto.Encrypt(userKey.FullKey, helloKey).ToString();

        return new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            new ServerOrigin(Server),
            1,
            Email,
            "device-id",
            "stored-refresh",
            protectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null)
        {
            PinProtectedUserKey = pinProtectedUserKey,
            PinFailedAttempts = 3,
            HelloProtectedUserKey = helloProtectedUserKey,
            HelloChallenge = Convert.ToBase64String(new byte[32]),
        };
    }

    private static void AssertLockedWithoutSecrets(VaultSession session)
    {
        Assert.Equal(VaultState.Locked, session.State);
        Assert.Null(session.UserKey);
        Assert.Null(session.AccessToken);
        Assert.Null(session.RefreshToken);
    }

    private static void AssertDecryptedKeyWasDisposed(CapturingCryptoService crypto)
    {
        Assert.NotNull(crypto.LastDecryptedUserKeyMaterial);
        Assert.All(crypto.LastDecryptedUserKeyMaterial!, value => Assert.Equal(0, value));
    }

    private enum LocalUnlockKind
    {
        Password,
        Pin,
        Hello,
    }

    public enum PersistenceFailureKind
    {
        IOException,
        UnauthorizedAccess,
        Cryptographic,
        InvalidData,
    }

    public enum LogoutRemovalFailureKind
    {
        ReturnedFalse,
        IOException,
        UnauthorizedAccess,
        Cryptographic,
        InvalidData,
    }

    private sealed class CapturingCryptoService : CryptoService
    {
        public SymmetricCryptoKey? LastDecryptedUserKey { get; private set; }
        public byte[]? LastDecryptedUserKeyMaterial { get; private set; }

        public override byte[] DecryptUserKeyMaterial(
            SymmetricCryptoKey stretchedKey,
            EncString protectedUserKey)
        {
            var decrypted = base.DecryptUserKeyMaterial(stretchedKey, protectedUserKey);
            LastDecryptedUserKeyMaterial = decrypted;
            return decrypted;
        }

        public override SymmetricCryptoKey DecryptUserKey(
            SymmetricCryptoKey stretchedKey,
            EncString protectedUserKey)
        {
            var decrypted = base.DecryptUserKey(stretchedKey, protectedUserKey);
            LastDecryptedUserKey = decrypted;
            return decrypted;
        }
    }

    private sealed class ThrowingSaveTokenStore(
        PersistedSession persisted,
        Exception saveFailure) : ITokenStore
    {
        public int SaveCount { get; private set; }

        public bool TryLoad(out PersistedSession session)
        {
            session = persisted;
            return true;
        }

        public void Save(PersistedSession session)
        {
            SaveCount++;
            throw saveFailure;
        }

        public void Clear()
        {
        }
    }

    private sealed class TrackingKeyConnectorClient : IKeyConnectorClient
    {
        public int CallCount { get; private set; }

        public Task<byte[]> GetMasterKeyAsync(
            string keyConnectorUrl,
            string accessToken,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new byte[32]);
        }
    }

    private sealed class FailingRemoveTokenStore(
        PersistedSession persisted,
        LogoutRemovalFailureKind failureKind,
        string detail) : ITokenStore
    {
        public int RemoveCount { get; private set; }
        public bool AllowRemoval { get; set; }

        public bool TryLoad(out PersistedSession session)
        {
            session = persisted;
            return true;
        }

        public void Save(PersistedSession session)
        {
        }

        public void Clear()
        {
        }

        public bool Remove(string accountId, ServerEndpoints endpoints)
        {
            RemoveCount++;
            if (AllowRemoval)
                return true;
            return failureKind switch
            {
                LogoutRemovalFailureKind.ReturnedFalse => false,
                LogoutRemovalFailureKind.IOException => throw new IOException(detail),
                LogoutRemovalFailureKind.UnauthorizedAccess =>
                    throw new UnauthorizedAccessException(detail),
                LogoutRemovalFailureKind.Cryptographic =>
                    throw new CryptographicException(detail),
                LogoutRemovalFailureKind.InvalidData =>
                    throw new InvalidDataException(detail),
                _ => throw new ArgumentOutOfRangeException(nameof(failureKind)),
            };
        }
    }
}
