using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Crypto;
using Crypto.V2;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class AccountServiceV2AndKdfTests
{
    private const string Email = "account-crypto@example.com";
    private const string Password = "current-master-password-1!";
    private const string NewPassword = "replacement-master-password-2!";
    private readonly CryptoService _crypto = new();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Verify_V2Password_ReturnsExpectedResult(bool correct)
    {
        using var fixture = Create(v2: true);

        var result = await fixture.Service.VerifyMasterPasswordAsync(
            correct ? Password : "incorrect-password", TestContext.Current.CancellationToken);

        Assert.Equal(correct, result);
        Assert.True(fixture.Session.IsUnlocked(fixture.Session.CaptureLease()));
    }

    [Fact]
    public async Task Verify_V2Password_RejectsADecryptedKeyDifferentFromTheActiveAccount()
    {
        using var fixture = Create(v2: true);
        using var unrelated = V2UserKey.Generate();
        var transport = unrelated.ExportTransport();
        var masterKey = Derive(Password, AccountKdfParameters.Pbkdf2());
        try
        {
            using var stretched = _crypto.StretchMasterKey(masterKey);
            Assert.True(fixture.Store.TryLoad(out var current));
            fixture.Store.Save(current with
            {
                ProtectedUserKey = _crypto.Encrypt(transport, stretched).ToString(),
                UserKeyId = unrelated.KeyIdHex,
            });
            Assert.False(await fixture.Service.VerifyMasterPasswordAsync(
                Password, TestContext.Current.CancellationToken));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            CryptographicOperations.ZeroMemory(transport);
        }
    }

    [Fact]
    public async Task ChangePassword_V2_RewrapsTheSameTransportAndIncludesTheKeyId()
    {
        using var fixture = Create(v2: true);

        await fixture.Service.ChangePasswordAsync(
            Password, NewPassword, "A reminder", TestContext.Current.CancellationToken);

        var request = Assert.IsType<ChangePasswordRequest>(fixture.Api.Password);
        Assert.Equal(fixture.KeyId, request.UnlockData!.ContainedKeyId);
        Assert.Equal(request.Key, request.UnlockData.MasterKeyWrappedUserKey);
        Assert.Equal(request.NewMasterPasswordHash,
            request.AuthenticationData!.MasterPasswordAuthenticationHash);
        AssertUnwraps(request.Key, NewPassword, AccountKdfParameters.Pbkdf2(), fixture.ExpectedTransport);
        Assert.False(fixture.Auth.LoggedOut);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ChangeKdf_PreservesV1OrV2KeysAcrossBothAlgorithmTransitions(
        bool v2,
        bool fromArgon2)
    {
        var original = fromArgon2 ? AccountKdfParameters.Argon2id(1, 15, 1) : AccountKdfParameters.Pbkdf2();
        var next = fromArgon2 ? AccountKdfParameters.Pbkdf2(700_000) : AccountKdfParameters.Argon2id(1, 15, 1);
        using var fixture = Create(v2, original);

        await fixture.Service.ChangeKdfAsync(Password, next, TestContext.Current.CancellationToken);

        var request = Assert.IsType<ChangeKdfRequest>(fixture.Api.Kdf);
        Assert.Equal((int)next.Type, request.AuthenticationData.Kdf.Kdf);
        Assert.Equal(next.Iterations, request.AuthenticationData.Kdf.KdfIterations);
        Assert.Equal(next.MemoryMiB, request.AuthenticationData.Kdf.KdfMemory);
        Assert.Equal(next.Parallelism, request.AuthenticationData.Kdf.KdfParallelism);
        Assert.Equal(request.AuthenticationData.Kdf, request.UnlockData.Kdf);
        Assert.Equal(fixture.KeyId, request.UnlockData.ContainedKeyId);
        Assert.Equal(Email, request.AuthenticationData.Salt);
        Assert.Equal(Email, request.UnlockData.Salt);
        AssertUnwraps(request.Key, Password, next, fixture.ExpectedTransport);
        Assert.False(fixture.Auth.LoggedOut);
    }

    [Theory]
    [InlineData(0, 64, 4)]
    [InlineData(11, 64, 4)]
    [InlineData(3, 14, 4)]
    [InlineData(3, 1025, 4)]
    [InlineData(3, 64, 0)]
    [InlineData(3, 64, 17)]
    [InlineData(3, null, 4)]
    [InlineData(3, 64, null)]
    public async Task ChangeKdf_InvalidArgon2Parameters_SendNoRequest(int iterations, int? memory, int? parallelism)
    {
        using var fixture = Create(v2: false);

        await Assert.ThrowsAsync<Core.Services.AccountOperationException>(() => fixture.Service.ChangeKdfAsync(
            Password, new AccountKdfParameters(KdfType.Argon2id, iterations, memory, parallelism),
            TestContext.Current.CancellationToken));

        Assert.Null(fixture.Api.Kdf);
        Assert.False(fixture.Auth.LoggedOut);
    }

    [Fact]
    public async Task ChangeKdf_CancelledOperation_PreservesTheSession()
    {
        using var fixture = Create(v2: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ChangeKdfAsync(
            Password, AccountKdfParameters.Argon2id(), cancellation.Token));

        Assert.Null(fixture.Api.Kdf);
        Assert.True(fixture.Session.IsUnlocked(fixture.Session.CaptureLease()));
    }

    private Fixture Create(bool v2, AccountKdfParameters? parameters = null)
    {
        parameters ??= AccountKdfParameters.Pbkdf2();
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-crypto", Email, new ServerOrigin("https://vault.example"));
        byte[] expectedTransport;
        string? keyId = null;
        PersistedAccountCryptographicState? state = null;
        if (v2)
        {
            using var material = V2AccountKeyMaterial.Generate();
            expectedTransport = material.UserKey.ExportTransport();
            keyId = material.UserKey.KeyIdHex;
            state = new PersistedAccountCryptographicState(
                2, material.WrappedPrivateKey, material.AccountPublicKey, material.SignedPublicKey,
                material.WrappedSigningKey, material.VerifyingKey, material.SecurityState);
            session.SetUnlockedKey(V2UserKey.ImportTransport(expectedTransport));
        }
        else
        {
            expectedTransport = RandomNumberGenerator.GetBytes(64);
            session.SetUnlockedKey(new SymmetricCryptoKey(expectedTransport.ToArray()));
        }
        session.SetTokens("access", "refresh");
        var masterKey = Derive(Password, parameters);
        string protectedKey;
        try
        {
            using var stretched = _crypto.StretchMasterKey(masterKey);
            protectedKey = _crypto.Encrypt(expectedTransport, stretched).ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
        var store = new MemoryTokenStore();
        store.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion, lease.AccountId, lease.Endpoints, lease.Generation,
            Email, "account-device", "refresh", protectedKey, parameters.Type, parameters.Iterations,
            parameters.MemoryMiB, parameters.Parallelism, Email)
        {
            HasMasterPassword = true,
            UserKeySecurityVersion = v2 ? 2 : 1,
            UserKeyId = keyId,
            AccountCryptographicState = state,
        });
        var api = new FakeAccountApiClient();
        var auth = new FakeAuthService(session, store);
        var cache = new VaultCacheService(new FakeCacheStore(), new VaultDecryptor(_crypto), session);
        var service = new AccountService(
            _crypto, FakeVaultApiClientProvider.ForAuthenticated<IAccountApiClient>(api),
            session, store, auth, cache);
        return new Fixture(service, session, store, api, auth, expectedTransport, keyId);
    }

    private byte[] Derive(string password, AccountKdfParameters parameters) =>
        _crypto.DeriveMasterKey(password, Email, parameters.Type, parameters.Iterations,
            parameters.MemoryMiB, parameters.Parallelism);

    private void AssertUnwraps(string wrapped, string password, AccountKdfParameters parameters, byte[] expected)
    {
        var masterKey = Derive(password, parameters);
        byte[]? actual = null;
        try
        {
            using var stretched = _crypto.StretchMasterKey(masterKey);
            actual = _crypto.DecryptUserKeyMaterial(stretched, EncString.Parse(wrapped));
            Assert.Equal(expected, actual);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            if (actual is not null)
                CryptographicOperations.ZeroMemory(actual);
        }
    }

    private sealed record Fixture(
        AccountService Service,
        VaultSession Session,
        MemoryTokenStore Store,
        FakeAccountApiClient Api,
        FakeAuthService Auth,
        byte[] ExpectedTransport,
        string? KeyId) : IDisposable
    {
        public void Dispose()
        {
            Session.Logout();
            Store.Clear();
            CryptographicOperations.ZeroMemory(ExpectedTransport);
        }
    }
}
