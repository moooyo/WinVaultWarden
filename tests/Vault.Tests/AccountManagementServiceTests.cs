using System.Net;
using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Crypto.V2;
using Xunit;

namespace Vault.Tests;

public sealed class AccountManagementServiceTests
{
    private const string Email = "original@example.com";
    private const string Password = "current-master-password-1!";
    private const string Salt = "Explicit-Master-Password-Salt";
    private readonly CryptoService _crypto = new();

    [Fact]
    public async Task RequestEmailChange_NormalizesAddressAndUsesExplicitCurrentSalt()
    {
        using var fixture = Create();
        await fixture.Service.RequestEmailChangeAsync(
            Password, " Next@Example.com ", TestContext.Current.CancellationToken);
        var request = Assert.IsType<AccountEmailTokenRequest>(fixture.Api.EmailToken);
        Assert.Equal("next@example.com", request.NewEmail);
        Assert.Equal(Hash(Password, Salt), request.MasterPasswordHash);
        Assert.True(fixture.Store.TryLoad(out _));
        Assert.Equal(VaultState.Unlocked, fixture.Session.State);
    }

    [Fact]
    public async Task ChangeEmail_ReprotectsTheSameKeyWithTheNewEmailSaltAndPurgesLocalSession()
    {
        using var fixture = Create();
        var expectedKey = fixture.Session.UserKey!.FullKey.ToArray();
        var result = await fixture.Service.ChangeEmailAsync(
            Password, " Next@Example.com ", " 123456 ", TestContext.Current.CancellationToken);
        var request = Assert.IsType<AccountEmailChangeRequest>(fixture.Api.EmailChange);
        Assert.Equal("next@example.com", request.NewEmail);
        Assert.Equal("123456", request.Token);
        Assert.Equal(Hash(Password, Salt), request.MasterPasswordHash);
        Assert.Equal(Hash(Password, request.NewEmail), request.NewMasterPasswordHash);
        var newMasterKey = Derive(Password, request.NewEmail);
        try
        {
            using var stretched = _crypto.StretchMasterKey(newMasterKey);
            using var actual = _crypto.DecryptUserKey(stretched, EncString.Parse(request.Key));
            Assert.Equal(expectedKey, actual.FullKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newMasterKey);
            CryptographicOperations.ZeroMemory(expectedKey);
        }
        Assert.Equal(AccountSecurityChangeOutcome.Confirmed, result.Outcome);
        Assert.True(result.LocalCleanupSucceeded);
        Assert.False(fixture.Store.TryLoad(out _));
        Assert.Single(fixture.Cache.ClearedBindings);
        Assert.Equal(VaultState.Locked, fixture.Session.State);
        Assert.NotNull(fixture.Notice);
    }

    [Fact]
    public async Task IncorrectPassword_DoesNotDispatchOrInvalidate()
    {
        using var fixture = Create();
        await Assert.ThrowsAsync<AccountOperationException>(() => fixture.Service.RevokeAllSessionsAsync(
            "wrong password", TestContext.Current.CancellationToken));
        Assert.Null(fixture.Api.Stamp);
        Assert.Equal(VaultState.Unlocked, fixture.Session.State);
        Assert.True(fixture.Store.TryLoad(out _));
    }

    [Fact]
    public async Task V2EmailChangeIsBlockedBeforeDispatchButSessionRevocationStillWorks()
    {
        using var fixture = Create();
        using var material = V2AccountKeyMaterial.Generate();
        var transport = material.UserKey.ExportTransport();
        var masterKey = Derive(Password, Salt);
        try
        {
            using var stretched = _crypto.StretchMasterKey(masterKey);
            Assert.True(fixture.Store.TryLoad(out var current));
            fixture.Store.Save(current with
            {
                UserKeySecurityVersion = 2,
                UserKeyId = material.UserKey.KeyIdHex,
                ProtectedUserKey = _crypto.Encrypt(transport, stretched).ToString(),
                AccountCryptographicState = new(2, material.WrappedPrivateKey,
                    material.AccountPublicKey, material.SignedPublicKey, material.WrappedSigningKey,
                    material.VerifyingKey, material.SecurityState),
            });
            fixture.Session.SetUnlockedKey(V2UserKey.ImportTransport(transport));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transport);
            CryptographicOperations.ZeroMemory(masterKey);
        }
        Assert.False(fixture.Service.GetAvailability().CanChangeEmail);
        await Assert.ThrowsAsync<AccountOperationException>(() => fixture.Service.RequestEmailChangeAsync(
            Password, "next@example.com", TestContext.Current.CancellationToken));
        Assert.Null(fixture.Api.EmailToken);
        var receipt = await fixture.Service.RevokeAllSessionsAsync(Password, TestContext.Current.CancellationToken);
        Assert.Equal(AccountSecurityChangeOutcome.Confirmed, receipt.Outcome);
        Assert.True(receipt.LocalCleanupSucceeded);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    public async Task RejectionAndUncertainServerFailureHaveDifferentLocalOutcomes(
        HttpStatusCode status, bool uncertain)
    {
        using var fixture = Create();
        fixture.Api.StampAction = _ => throw new VaultWriteException(status, "Rejected", null);
        if (!uncertain)
        {
            await Assert.ThrowsAsync<VaultWriteException>(() => fixture.Service.RevokeAllSessionsAsync(
                Password, TestContext.Current.CancellationToken));
            Assert.Equal(VaultState.Unlocked, fixture.Session.State);
            Assert.True(fixture.Store.TryLoad(out _));
            return;
        }
        var result = await fixture.Service.RevokeAllSessionsAsync(Password, TestContext.Current.CancellationToken);
        Assert.Equal(AccountSecurityChangeOutcome.Uncertain, result.Outcome);
        Assert.True(result.LocalCleanupSucceeded);
        Assert.False(fixture.Store.TryLoad(out _));
    }

    [Fact]
    public async Task ResponseLoss_ReturnsUncertainReceiptAndDoesNotReplay()
    {
        using var fixture = Create();
        fixture.Api.StampAction = _ => throw new TaskCanceledException("Response lost");
        var result = await fixture.Service.RevokeAllSessionsAsync(Password, TestContext.Current.CancellationToken);
        Assert.Equal(AccountSecurityChangeOutcome.Uncertain, result.Outcome);
        Assert.Equal(1, fixture.Api.StampCalls);
        Assert.False(fixture.Store.TryLoad(out _));
    }

    [Fact]
    public async Task StaleSuccessfulRevocationDoesNotClearANewAccount()
    {
        using var fixture = Create();
        fixture.Api.StampAction = _ =>
        {
            fixture.Session.BeginAccountSession("new-account", "new@example.com", fixture.Endpoints);
            fixture.Session.SetUnlockedKey(new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64)));
            return Task.CompletedTask;
        };
        var result = await fixture.Service.RevokeAllSessionsAsync(Password, TestContext.Current.CancellationToken);
        Assert.Equal(AccountSecurityChangeOutcome.Confirmed, result.Outcome);
        Assert.Null(result.InvalidatedSession);
        Assert.False(result.LocalCleanupSucceeded);
        Assert.Equal("new-account", fixture.Session.Account.Id);
        Assert.Equal(VaultState.Unlocked, fixture.Session.State);
        Assert.Empty(fixture.Cache.ClearedBindings);
    }

    [Fact]
    public async Task StorageFailureStillInvalidatesAndSignalsTheSession()
    {
        using var fixture = Create();
        fixture.Cache.BeforeClear = () => throw new IOException("Unavailable");
        var result = await fixture.Service.RevokeAllSessionsAsync(Password, TestContext.Current.CancellationToken);
        Assert.Equal(AccountSecurityChangeOutcome.Confirmed, result.Outcome);
        Assert.False(result.LocalCleanupSucceeded);
        Assert.Equal(VaultState.Locked, fixture.Session.State);
        Assert.NotNull(fixture.Notice);
    }

    [Fact]
    public async Task VerificationUsesTheCurrentAccountIdAndTheActualToken()
    {
        using var fixture = Create();
        await fixture.Service.RequestEmailVerificationAsync(TestContext.Current.CancellationToken);
        await fixture.Service.VerifyEmailAsync(
            "https://vault.example/#/verify-email?userId=account&token=token.jwt.value",
            TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Api.VerificationRequests);
        Assert.Equal("account", fixture.Api.Verification!.UserId);
        Assert.Equal("token.jwt.value", fixture.Api.Verification.Token);
    }

    [Theory]
    [InlineData("next@example.com\r\nOther: value")]
    [InlineData("Display Name <next@example.com>")]
    [InlineData("original@example.com")]
    public async Task InvalidEmailDoesNotDispatch(string email)
    {
        using var fixture = Create();
        await Assert.ThrowsAsync<AccountOperationException>(() => fixture.Service.RequestEmailChangeAsync(
            Password, email, TestContext.Current.CancellationToken));
        Assert.Null(fixture.Api.EmailToken);
    }

    [Theory]
    [InlineData("https://vault.example/#/verify-email?userId=other&token=token")]
    [InlineData("https://vault.example/?token=one&token=two")]
    [InlineData("https://vault.example/?userId=account")]
    [InlineData("file:///somewhere?token=token")]
    public void InvalidVerificationLinkIsRejected(string link) =>
        Assert.Throws<AccountOperationException>(() => AccountManagementService.ParseVerificationToken(link, "account"));

    private Fixture Create()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account", Email, endpoints);
        using var userKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var masterKey = Derive(Password, Salt);
        string wrapped;
        try { wrapped = _crypto.ProtectUserKey(masterKey, userKey).ToString(); }
        finally { CryptographicOperations.ZeroMemory(masterKey); }
        session.SetUnlockedKey(new SymmetricCryptoKey(userKey.FullKey.ToArray()));
        session.SetTokens("access", "refresh");
        var store = new MemoryTokenStore();
        store.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, lease.AccountId, endpoints,
            lease.Generation, Email, "device", "refresh", wrapped, KdfType.Pbkdf2, 600_000, null, null, Salt));
        var cacheStore = new FakeCacheStore();
        var cache = new VaultCacheService(cacheStore, new VaultDecryptor(_crypto), session);
        var accountService = new AccountService(_crypto, null!, session, store, null!, cache);
        var api = new ManagementApi();
        var signal = new SessionTerminationSignal();
        var fixture = new Fixture(session, store, cacheStore, api, endpoints);
        signal.TerminationRequested += notice => fixture.Notice = notice;
        fixture.Service = new(_crypto, accountService,
            FakeVaultApiClientProvider.ForAuthenticated<IAccountManagementApiClient>(api),
            session, store, cache, signal);
        return fixture;
    }

    private byte[] Derive(string password, string salt) =>
        _crypto.DeriveMasterKey(password, salt, KdfType.Pbkdf2, 600_000, null, null);

    private string Hash(string password, string salt)
    {
        var key = Derive(password, salt);
        try { return _crypto.ComputeMasterPasswordHash(key, password); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private sealed class Fixture(VaultSession session, MemoryTokenStore store, FakeCacheStore cache,
        ManagementApi api, ServerEndpoints endpoints) : IDisposable
    {
        public VaultSession Session { get; } = session;
        public MemoryTokenStore Store { get; } = store;
        public FakeCacheStore Cache { get; } = cache;
        public ManagementApi Api { get; } = api;
        public ServerEndpoints Endpoints { get; } = endpoints;
        public SessionTerminationNotice? Notice { get; set; }
        public AccountManagementService Service { get; set; } = null!;
        public void Dispose() => Session.Logout();
    }

    private sealed class ManagementApi : IAccountManagementApiClient
    {
        public AccountEmailTokenRequest? EmailToken { get; private set; }
        public AccountEmailChangeRequest? EmailChange { get; private set; }
        public AccountSecurityStampRequest? Stamp { get; private set; }
        public AccountEmailVerificationRequest? Verification { get; private set; }
        public int VerificationRequests { get; private set; }
        public int StampCalls { get; private set; }
        public Func<CancellationToken, Task> StampAction { get; set; } = _ => Task.CompletedTask;
        public Task RequestEmailChangeAsync(AccountEmailTokenRequest request, CancellationToken ct = default)
        { EmailToken = request; return Task.CompletedTask; }
        public Task ChangeEmailAsync(AccountEmailChangeRequest request, CancellationToken ct = default)
        { EmailChange = request; return Task.CompletedTask; }
        public Task RequestEmailVerificationAsync(CancellationToken ct = default)
        { VerificationRequests++; return Task.CompletedTask; }
        public Task VerifyEmailTokenAsync(AccountEmailVerificationRequest request, CancellationToken ct = default)
        { Verification = request; return Task.CompletedTask; }
        public Task RevokeAllSessionsAsync(AccountSecurityStampRequest request, CancellationToken ct = default)
        { Stamp = request; StampCalls++; return StampAction(ct); }
    }
}
