using System.Net;
using System.Text;
using System.Text.Json;
using Core.Models;
using Xunit;

namespace Api.Tests;

public sealed class KnownDeviceWireTests
{
    private const string DeviceIdentifier = "11111111-2222-3333-4444-555555555555";

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task Lookup_UsesExactHeadersAndStrictBooleanResponse(
        string responseBody,
        bool expected)
    {
        var handler = new RecordingHandler(responseBody);
        using var api = CreateApi(handler);

        var result = await ((IPublicAuthRequestApiClient)api).GetKnownDeviceAsync(
            "test@example.com",
            DeviceIdentifier.ToUpperInvariant(),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("/api/devices/knowndevice", handler.RequestUri!.AbsolutePath);
        Assert.Equal(
            "dGVzdEBleGFtcGxlLmNvbQ",
            Assert.Single(handler.Headers["X-Request-Email"]));
        Assert.DoesNotContain('=', Assert.Single(handler.Headers["X-Request-Email"]));
        Assert.Equal(
            DeviceIdentifier,
            Assert.Single(handler.Headers["X-Device-Identifier"]));
    }

    [Fact]
    public async Task Lookup_RejectsNonBooleanSuccessPayload()
    {
        var handler = new RecordingHandler("\"true\"");
        using var api = CreateApi(handler);

        await Assert.ThrowsAsync<JsonException>(() =>
            ((IPublicAuthRequestApiClient)api).GetKnownDeviceAsync(
                "test@example.com",
                DeviceIdentifier,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lookup_PropagatesHttpErrors()
    {
        var handler = new RecordingHandler(
            """{"message":"invalid known-device headers"}""",
            HttpStatusCode.BadRequest);
        using var api = CreateApi(handler);

        var exception = await Assert.ThrowsAsync<VaultApiException>(() =>
            ((IPublicAuthRequestApiClient)api).GetKnownDeviceAsync(
                "test@example.com",
                DeviceIdentifier,
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("invalid known-device headers", exception.ServerMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111222233334444555555555555")]
    [InlineData("not-a-guid")]
    public async Task Lookup_RejectsInvalidDeviceIdentifier(string deviceIdentifier)
    {
        var handler = new RecordingHandler("true");
        using var api = CreateApi(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ((IPublicAuthRequestApiClient)api).GetKnownDeviceAsync(
                "test@example.com",
                deviceIdentifier,
                TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.CallCount);
    }

    private static ApiClient CreateApi(RecordingHandler handler) =>
        new(
            new HttpClient(handler),
            new ServerOrigin("https://vault.example"));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;

        public RecordingHandler(
            string responseBody,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        public int CallCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public Dictionary<string, string[]> Headers { get; private set; } =
            new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            CallCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Headers = request.Headers.ToDictionary(
                header => header.Key,
                header => header.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
