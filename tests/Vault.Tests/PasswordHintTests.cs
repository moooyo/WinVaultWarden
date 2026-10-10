using System.Net;
using System.Text.Json;
using Api;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Session;
using Crypto;
using Xunit;

namespace Vault.Tests;

public sealed class PasswordHintTests
{
    [Theory]
    [InlineData("https://vault.bitwarden.com", "https://api.bitwarden.com/accounts/password-hint")]
    [InlineData("https://vault.bitwarden.eu", "https://api.bitwarden.eu/accounts/password-hint")]
    [InlineData("https://vault.example/team", "https://vault.example/team/api/accounts/password-hint")]
    public async Task PasswordHint_PostsOnlyEmailToTheAnonymousApiEndpoint(
        string serverUrl,
        string expectedUrl)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var factory = new ApiClientFactory(authenticated =>
        {
            Assert.False(authenticated);
            return handler;
        });
        using var client = Assert.IsType<ApiClient>(factory.CreateAnonymous<IIdentityApiClient>(
            ServerEndpoints.FromWebVault(serverUrl)));

        await client.RequestPasswordHintAsync("person@example.com", TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(expectedUrl, request.RequestUri!.AbsoluteUri);
        Assert.Null(request.Headers.Authorization);
        using var json = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var email = Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal("email", email.Name);
        Assert.Equal("person@example.com", email.Value.GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task PasswordHint_AcceptsAnEmptySuccessResponse(HttpStatusCode status)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(status));
        using var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

        await client.RequestPasswordHintAsync("person@example.com", TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("This server is not configured to provide password hints.")]
    [InlineData("Sorry, you have no password hint...")]
    [InlineData("Your password hint is: the first computer")]
    public async Task PasswordHint_PreservesTheServerMessageIncludingAnInlineHint(string message)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            JsonSerializer.Serialize(new { message })));
        using var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

        var error = await Assert.ThrowsAsync<VaultWriteException>(() =>
            client.RequestPasswordHintAsync("person@example.com", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal(message, error.ServerMessage);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AuthService_PasswordHintPreservesTheSavedSessionAndAuthenticationAttempt()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var session = new VaultSession();
        var store = new MemoryTokenStore();
        var saved = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "saved-account",
            ServerEndpoints.FromWebVault("https://saved.example"),
            7,
            "saved@example.com",
            "saved-device",
            "saved-refresh-token",
            "saved-protected-key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null);
        store.Save(saved);
        var savedLease = session.RestorePersistedSession(saved);
        session.SetTokens("saved-access-token", saved.RefreshToken);
        using var provider = CreateProvider(handler, session);
        var service = CreateService(provider, session, store);
        var attempt = service.BeginAuthenticationAttempt();

        await service.RequestPasswordHintAsync(
            "https://vault.bitwarden.eu",
            " person@example.com ",
            TestContext.Current.CancellationToken);

        Assert.True(session.IsCurrent(attempt));
        Assert.True(session.IsCurrent(savedLease));
        Assert.Equal(VaultState.Locked, session.State);
        Assert.Equal("saved-access-token", session.AccessToken);
        Assert.True(store.TryLoad(out var after));
        Assert.Equal(saved, after);
        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("https://api.bitwarden.eu/accounts/password-hint", request.RequestUri!.AbsoluteUri);
        using var json = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("person@example.com", json.RootElement.GetProperty("email").GetString());
    }

    [Fact]
    public async Task AuthService_PasswordHintPropagatesServerRejectionWithoutRetrying()
    {
        const string message = "Your password hint is: the first computer";
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            JsonSerializer.Serialize(new { message })));
        var session = new VaultSession();
        var store = new MemoryTokenStore();
        using var provider = CreateProvider(handler, session);
        var service = CreateService(provider, session, store);
        var attempt = service.BeginAuthenticationAttempt();

        var error = await Assert.ThrowsAsync<VaultWriteException>(() => service.RequestPasswordHintAsync(
            "https://vault.example",
            "person@example.com",
            TestContext.Current.CancellationToken));

        Assert.Equal(message, error.ServerMessage);
        Assert.True(session.IsCurrent(attempt));
        Assert.False(store.TryLoad(out _));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AuthService_PasswordHintWithCancelledTokenDoesNotSend()
    {
        var handler = new FakeHttpMessageHandler();
        var session = new VaultSession();
        using var provider = CreateProvider(handler, session);
        var service = CreateService(provider, session, new MemoryTokenStore());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RequestPasswordHintAsync(
            "https://vault.example",
            "person@example.com",
            cancellation.Token));

        Assert.Empty(handler.Requests);
    }

    private static VaultApiClientProvider CreateProvider(FakeHttpMessageHandler handler, VaultSession session) =>
        new(new ApiClientFactory(authenticated =>
        {
            Assert.False(authenticated);
            return handler;
        }), session, (_, _, _) => Task.FromResult(false));

    private static AuthService CreateService(
        IVaultApiClientProvider provider,
        VaultSession session,
        ITokenStore store)
    {
        var crypto = new CryptoService();
        var decryptor = new VaultDecryptor(crypto);
        var cache = new VaultCacheService(new FakeCacheStore(), decryptor, session);
        var bootstrapper = new VaultBootstrapper(new SyncService(provider, decryptor, session, cache), provider, session);
        return new AuthService(provider, crypto, session, store, bootstrapper, cache,
            new TokenRefreshService(provider, session, store));
    }
}
