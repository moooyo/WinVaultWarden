using System.Net;
using Api;
using Core.Models;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class VaultApiClientProviderTests
{
    [Fact]
    public async Task OldAClient_AfterSwitchToB_CannotReadAToken()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            "{\"object\":\"sync\",\"profile\":null,\"folders\":[],\"ciphers\":[]}"));
        var session = new VaultSession();
        var a = session.BeginAccountSession(
            "account-a", "a@example.com", new ServerOrigin("https://a.example"));
        session.TryCommit(a, () => session.SetTokens("token-a", "refresh-a"));
        var provider = new VaultApiClientProvider(
            new ApiClientFactory(_ => handler),
            session,
            (_, _) => Task.FromResult(false));
        var oldClient = provider.GetAuthenticated<IReadonlyApiClient>(a);

        session.BeginAccountSession(
            "account-b", "b@example.com", new ServerOrigin("https://b.example"));
        await oldClient.GetSyncAsync();

        Assert.Null(handler.Requests.Single().Headers.Authorization);
    }

    [Fact]
    public void GetAuthenticated_RejectsStaleLeaseBeforeClientCreation()
    {
        var session = new VaultSession();
        var stale = session.BeginAccountSession(
            "a", "a@example.com", new ServerOrigin("https://a.example"));
        session.Lock();
        var provider = new VaultApiClientProvider(
            new ApiClientFactory(),
            session,
            (_, _) => Task.FromResult(false));

        Assert.Throws<OperationCanceledException>(() =>
            provider.GetAuthenticated<IReadonlyApiClient>(stale));
    }
}
