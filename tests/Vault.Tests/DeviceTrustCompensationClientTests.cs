using System.Net;
using System.Net.Http.Headers;
using Api;
using Core.Models;
using Core.Session;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class DeviceTrustCompensationClientTests
{
    [Fact]
    public async Task UntrustAsync_AfterLeaseExpiresUsesFrozenBearerAndEndpoint()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var factory = new ApiClientFactory(_ => handler);
        var binding = new AccessTokenBinding(
            "account-id",
            ServerEndpoints.FromWebVault("https://vault.example/base"),
            4,
            "frozen-access-token");
        var client = new DeviceTrustCompensationClient(factory);

        await client.UntrustAsync(
            binding,
            "server-device-id",
            TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/base/api/devices/untrust", request.RequestUri!.AbsolutePath);
        Assert.Equal(
            new AuthenticationHeaderValue("Bearer", "frozen-access-token"),
            request.Headers.Authorization);
        Assert.Equal(
            "{\"devices\":[\"server-device-id\"]}",
            handler.Bodies.Single());
    }
}
