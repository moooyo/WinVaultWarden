using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Crypto.V2;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class AuthServiceV2LocalUnlockTests
{
    private const string Server = "https://vault.example";
    private const string Email = "v2@example.com";
    private const string AccountId = "account-v2";
    private const string Password = "correct horse battery staple";
    private const string Pin = "123456";
    private const int Iterations = 600_000;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalUnlockRestoresV2KeyAndAuthenticatedAccountState(bool usePin)
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        using var fixture = new Fixture(material, usePin);
        await fixture.Auth.LockAsync(TestContext.Current.CancellationToken);

        var result = await fixture.UnlockAsync(usePin);

        Assert.IsType<AuthResult.Success>(result);
        Assert.True(fixture.Session.UsesV2UserKey);
        Assert.Equal(2, fixture.Session.SecurityVersion);
        Assert.Null(fixture.Session.UserKey);
        Assert.Equal(VaultState.Unlocked, fixture.Session.State);
        Assert.Equal(material.WrappedPrivateKey, fixture.Session.EncryptedPrivateKey);
        Assert.True(fixture.Store.TryLoad(out var persisted));
        Assert.Equal(material.UserKey.KeyIdHex, persisted.UserKeyId);
        Assert.Equal(0, persisted.PinFailedAttempts);
        Assert.Equal("rotated-refresh", persisted.RefreshToken);
        Assert.Equal(1, fixture.Identity.ConnectCallCount);
    }

    [Fact]
    public async Task WrongV2PinIncrementsFailureCountWithoutNetworkOrKeyTransfer()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        using var fixture = new Fixture(material, usePin: true);
        await fixture.Auth.LockAsync(TestContext.Current.CancellationToken);

        var result = await fixture.Auth.UnlockWithPinAsync(
            "999999",
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(0, fixture.Identity.ConnectCallCount);
        Assert.False(fixture.Session.UsesV2UserKey);
        Assert.Null(fixture.Session.UserKey);
        Assert.True(fixture.Store.TryLoad(out var persisted));
        Assert.Equal(1, persisted.PinFailedAttempts);
        Assert.NotNull(persisted.PinProtectedUserKey);
    }

    [Fact]
    public async Task WrongV2HelloSignatureClearsProtectorAndCallerSignatureWithoutNetwork()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        using var fixture = new Fixture(material, usePin: false);
        await fixture.Auth.LockAsync(TestContext.Current.CancellationToken);
        var signature = Signature(9);

        var result = await fixture.Auth.UnlockWithHelloAsync(
            signature,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Failure>(result);
        Assert.All(signature, value => Assert.Equal(0, value));
        Assert.Equal(0, fixture.Identity.ConnectCallCount);
        Assert.False(fixture.Session.UsesV2UserKey);
        Assert.Null(fixture.Session.UserKey);
        Assert.True(fixture.Store.TryLoad(out var persisted));
        Assert.Null(persisted.HelloProtectedUserKey);
        Assert.Null(persisted.HelloChallenge);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalUnlockRejectsTamperedV2SignedStateBeforeNetwork(bool usePin)
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        using var fixture = new Fixture(material, usePin);
        await fixture.Auth.LockAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.Store.TryLoad(out var persisted));
        var state = Assert.IsType<PersistedAccountCryptographicState>(
            persisted.AccountCryptographicState);
        var signedState = Convert.FromBase64String(state.SignedSecurityState!);
        try
        {
            signedState[^1] ^= 0x01;
            fixture.Store.Save(persisted with
            {
                AccountCryptographicState = state with
                {
                    SignedSecurityState = Convert.ToBase64String(signedState),
                },
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signedState);
        }

        var result = await fixture.UnlockAsync(usePin);

        Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(0, fixture.Identity.ConnectCallCount);
        Assert.False(fixture.Session.UsesV2UserKey);
        Assert.Null(fixture.Session.UserKey);
        Assert.Null(fixture.Session.AccessToken);
        Assert.Null(fixture.Session.RefreshToken);
    }

    private static byte[] Signature(byte value = 5) =>
        Enumerable.Repeat(value, 256).ToArray();

    private static string AccessToken()
    {
        static string Encode(string value) => Convert
            .ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var header = Encode("""{"alg":"none"}""");
        var payload = Encode($$"""{"sub":"{{AccountId}}","email":"{{Email}}"}""");
        return $"{header}.{payload}.signature";
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(V2AccountKeyMaterial material, bool usePin)
        {
            var crypto = new CryptoService();
            var lease = Session.BeginAccountSession(
                AccountId,
                Email,
                ServerEndpoints.FromWebVault(Server));
            Session.SetTokens("old-access", "old-refresh");
            var transport = material.UserKey.ExportTransport();
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
                Session.SetUnlockedKey(V2UserKey.ImportTransport(transport));
                using var stretched = crypto.StretchMasterKey(masterKey);
                protectedUserKey = crypto.Encrypt(transport, stretched).ToString();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(transport);
                CryptographicOperations.ZeroMemory(masterKey);
            }

            var accountKeys = new AccountKeysResponse(
                new PublicKeyEncryptionKeyPairResponse(
                    material.WrappedPrivateKey,
                    material.AccountPublicKey,
                    material.SignedPublicKey),
                new SignatureKeyPairResponse(
                    material.WrappedSigningKey,
                    material.VerifyingKey),
                new AccountSecurityState(
                    material.SecurityState,
                    material.SecurityVersion));
            Store.Save(new PersistedSession(
                PersistedSession.CurrentSchemaVersion,
                AccountId,
                lease.Endpoints,
                lease.Generation,
                Email,
                "11111111-2222-3333-4444-555555555555",
                "old-refresh",
                protectedUserKey,
                KdfType.Pbkdf2,
                Iterations,
                null,
                null,
                Email)
            {
                HasMasterPassword = true,
                UserKeySecurityVersion = 2,
                UserKeyId = material.UserKey.KeyIdHex,
                AccountCryptographicState = new PersistedAccountCryptographicState(
                    2,
                    material.WrappedPrivateKey,
                    material.AccountPublicKey,
                    material.SignedPublicKey,
                    material.WrappedSigningKey,
                    material.VerifyingKey,
                    material.SecurityState),
            });

            if (usePin)
                new PinService(crypto, Session, Store).SetPin(Pin);
            else
                new HelloUnlockService(crypto, Session, Store).SetHello(Signature(), new byte[32]);

            Identity = new ControlledIdentityApiClient(_ =>
                Task.FromResult<ConnectTokenResult>(new ConnectTokenResult.Success(
                    new TokenResponse(
                        AccessToken(),
                        "rotated-refresh",
                        3600,
                        "Bearer",
                        "api offline_access",
                        null,
                        null,
                        KdfType.Pbkdf2,
                        Iterations))));
            var sync = new SyncResponse(
                "sync",
                new ProfileDto(AccountId, Email, "V2 User", null, null)
                {
                    AccountKeys = JsonSerializer.SerializeToElement(
                        accountKeys,
                        ApiJsonContext.Default.AccountKeysResponse),
                    SecurityStamp = "stamp-v2",
                },
                [],
                []);
            var read = new ControlledReadonlyApiClient(_ => Task.FromResult(sync))
            {
                GetConfig = _ => Task.FromResult(new ConfigResponse(null, null, null)),
                GetDevices = _ => Task.FromResult(new ListResponse<DeviceDto>([], null, "list")),
            };
            var provider = new FakeVaultApiClientProvider()
                .AddAnonymous<IIdentityApiClient>(Identity)
                .AddAuthenticated<IReadonlyApiClient>(read)
                .AddAuthenticated<IServerConfigApiClient>(read);
            var decryptor = new VaultDecryptor(crypto);
            var cache = new VaultCacheService(new FakeCacheStore(), decryptor, Session);
            var syncService = new SyncService(provider, decryptor, Session, cache, Store);
            var bootstrapper = new VaultBootstrapper(syncService, provider, Session);
            var refresher = new TokenRefreshService(provider, Session, Store);
            Auth = new AuthService(provider, crypto, Session, Store, bootstrapper, cache, refresher);
        }

        public VaultSession Session { get; } = new();
        public MemoryTokenStore Store { get; } = new();
        public ControlledIdentityApiClient Identity { get; }
        public AuthService Auth { get; }

        public Task<AuthResult> UnlockAsync(bool usePin) => usePin
            ? Auth.UnlockWithPinAsync(Pin, TestContext.Current.CancellationToken)
            : Auth.UnlockWithHelloAsync(Signature(), TestContext.Current.CancellationToken);

        public void Dispose() => Session.Logout();
    }
}
