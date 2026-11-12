using System.Net;
using System.Security.Cryptography;
using Api;
using Core.Enums;
using Core.Models;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class CredentialPersistenceRegressionTests
{
    private const string Email = "me@example.com";
    private const string Server = "https://vault.example";
    private const string RefreshJson =
        """{"access_token":"e30.eyJzdWIiOiJtZUBleGFtcGxlLmNvbSJ9.c2ln","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}""";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshDoesNotRestoreARevokedLocalUnlockCredential(bool revokePin)
    {
        using var fixture = new Fixture();
        fixture.Pin.SetPin("123456");
        fixture.Hello.SetHello(Signature(), new byte[32]);
        var refresh = fixture.RefreshAsync();
        await fixture.RequestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        if (revokePin)
            fixture.Pin.ClearPin();
        else
            fixture.Hello.ClearHello();

        Assert.True(fixture.Store.TryLoad(out var before));
        Assert.Null(revokePin ? before.PinProtectedUserKey : before.HelloProtectedUserKey);

        fixture.CompleteRefresh();
        Assert.True(await refresh.WaitAsync(TestContext.Current.CancellationToken));
        Assert.True(fixture.Store.TryLoad(out var after));
        Assert.Null(revokePin ? after.PinProtectedUserKey : after.HelloProtectedUserKey);
        Assert.Equal(before.PinProtectedUserKey, after.PinProtectedUserKey);
        Assert.Equal(before.HelloProtectedUserKey, after.HelloProtectedUserKey);
        Assert.Equal(before.HelloChallenge, after.HelloChallenge);
        Assert.Equal("new-refresh", after.RefreshToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshDoesNotDiscardANewLocalUnlockCredential(bool enablePin)
    {
        using var fixture = new Fixture();
        var refresh = fixture.RefreshAsync();
        await fixture.RequestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        if (enablePin)
            fixture.Pin.SetPin("123456");
        else
            fixture.Hello.SetHello(Signature(), new byte[32]);

        Assert.True(fixture.Store.TryLoad(out var before));
        Assert.NotNull(enablePin ? before.PinProtectedUserKey : before.HelloProtectedUserKey);

        fixture.CompleteRefresh();
        Assert.True(await refresh.WaitAsync(TestContext.Current.CancellationToken));
        Assert.True(fixture.Store.TryLoad(out var after));
        Assert.Equal(before.PinProtectedUserKey, after.PinProtectedUserKey);
        Assert.Equal(before.HelloProtectedUserKey, after.HelloProtectedUserKey);
        Assert.Equal(before.HelloChallenge, after.HelloChallenge);
        Assert.Equal("new-refresh", after.RefreshToken);
    }

    [Fact]
    public async Task RefreshPreservesLatestSameSessionMetadataWhenResponseOmitsIt()
    {
        using var fixture = new Fixture();
        var refresh = fixture.RefreshAsync();
        await fixture.RequestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.Session.TryCommit(fixture.Session.CaptureLease(), () =>
        {
            Assert.True(fixture.Store.TryLoad(out var latest));
            fixture.Store.Save(latest with
            {
                TwoFactorToken = "new-two-factor-token",
                HasMasterPassword = true,
                SupportsDeviceTrust = true,
                PinFailedAttempts = 2,
            });
        }));

        fixture.CompleteRefresh();
        Assert.True(await refresh.WaitAsync(TestContext.Current.CancellationToken));
        Assert.True(fixture.Store.TryLoad(out var persisted));
        Assert.Equal("new-two-factor-token", persisted.TwoFactorToken);
        Assert.True(persisted.HasMasterPassword);
        Assert.True(persisted.SupportsDeviceTrust);
        Assert.Equal(2, persisted.PinFailedAttempts);
        Assert.Equal("new-refresh", persisted.RefreshToken);
    }

    private static byte[] Signature() => Enumerable.Repeat((byte)5, 256).ToArray();

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        private readonly TokenRefreshService _refresher;
        private readonly TaskCompletionSource<HttpResponseMessage> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture()
        {
            var crypto = new CryptoService();
            var lease = Session.BeginAccountSession(Email, Email, new ServerOrigin(Server));
            Session.SetTokens("old-access", "old-refresh");
            Session.SetUnlockedKey(new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64)));
            Store.Save(new PersistedSession(
                PersistedSession.CurrentSchemaVersion,
                lease.AccountId,
                lease.Endpoints,
                lease.Generation,
                Email,
                "device-id",
                "old-refresh",
                "2.protected",
                KdfType.Pbkdf2,
                600_000,
                null,
                null)
            {
                HasMasterPassword = false,
            });
            Pin = new PinService(crypto, Session, Store);
            Hello = new HelloUnlockService(crypto, Session, Store);

            var handler = new FakeHttpMessageHandler();
            handler.Enqueue((_, _) =>
            {
                RequestStarted.TrySetResult();
                return _response.Task;
            });
            _http = new HttpClient(handler);
            var api = new ApiClient(_http, new ServerOrigin(Server));
            _refresher = new TokenRefreshService(
                FakeVaultApiClientProvider.ForAnonymous<IIdentityApiClient>(api),
                Session,
                Store);
        }

        public VaultSession Session { get; } = new();
        public MemoryTokenStore Store { get; } = new();
        public PinService Pin { get; }
        public HelloUnlockService Hello { get; }
        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> RefreshAsync() => _refresher.TryRefreshAsync(
            Session.CaptureLease(),
            TestContext.Current.CancellationToken);

        public void CompleteRefresh()
        {
            var response = FakeHttpMessageHandler.Json(HttpStatusCode.OK, RefreshJson);
            if (!_response.TrySetResult(response))
                response.Dispose();
        }

        public void Dispose()
        {
            CompleteRefresh();
            Session.Logout();
            _http.Dispose();
        }
    }
}
