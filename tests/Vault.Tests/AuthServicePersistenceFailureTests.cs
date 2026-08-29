using System.Security.Cryptography;
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

    private static AuthService CreateAuthService(
        CryptoService crypto,
        VaultSession session,
        ITokenStore tokenStore)
    {
        var cache = new VaultCacheService(
            new FakeCacheStore(),
            new VaultDecryptor(crypto),
            session);
        return new AuthService(
            new FakeVaultApiClientProvider(),
            crypto,
            session,
            tokenStore,
            bootstrapper: null!,
            cache,
            refresher: null!);
    }

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
        Assert.NotNull(crypto.LastDecryptedUserKey);
        Assert.All(
            crypto.LastDecryptedUserKey!.FullKey,
            value => Assert.Equal(0, value));
    }

    private enum LocalUnlockKind
    {
        Password,
        Pin,
        Hello,
    }

    private sealed class CapturingCryptoService : CryptoService
    {
        public SymmetricCryptoKey? LastDecryptedUserKey { get; private set; }

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
}
