using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Core.Models;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class KeyConnectorClientTests
{
    [Theory]
    [InlineData("Key")]
    [InlineData("key")]
    public async Task GetMasterKey_AcceptsOfficialAndCompatibleResponseCasing(string propertyName)
    {
        var expected = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            $$"""{"{{propertyName}}":"{{Convert.ToBase64String(expected)}}"}"""));
        using var client = new KeyConnectorClient(new HttpClient(handler));

        var result = await client.GetMasterKeyAsync(
            "https://keys.example/base",
            "access-token",
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://keys.example/base/user-keys", request.RequestUri!.AbsoluteUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "access-token"), request.Headers.Authorization);
        Assert.True(request.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("http://keys.example")]
    [InlineData("https://user:password@keys.example")]
    [InlineData("https://keys.example?target=other")]
    public async Task GetMasterKey_RejectsUnsafeEndpointBeforeNetwork(string endpoint)
    {
        var handler = new FakeHttpMessageHandler();
        using var client = new KeyConnectorClient(new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetMasterKeyAsync(
            endpoint,
            "access-token",
            TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetMasterKey_RejectsWrongKeyLength()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new { key = Convert.ToBase64String(new byte[64]) })));
        using var client = new KeyConnectorClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.GetMasterKeyAsync(
            "https://keys.example",
            "access-token",
            TestContext.Current.CancellationToken));

        Assert.Contains("not supported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
