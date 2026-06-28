using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using BrowserNativeHost;
using Core.Passkeys;
using Xunit;

namespace BrowserNativeHost.Tests;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NativeMessageProtocolTests.SampleMessage))]
internal partial class TestJsonContext : JsonSerializerContext;

public class NativeMessageProtocolTests
{
    public sealed record SampleMessage(string Type, string Value);

    [Fact]
    public async Task WriteRead_RoundTripsLengthPrefixedJson()
    {
        var stream = new MemoryStream();

        await NativeMessageProtocol.WriteAsync(stream, new SampleMessage("ping", "hello"), TestJsonContext.Default.SampleMessage, TestContext.Current.CancellationToken);

        var bytes = stream.ToArray();
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        Assert.Equal((uint)(bytes.Length - 4), payloadLength);

        stream.Position = 0;
        var read = await NativeMessageProtocol.ReadAsync(stream, TestJsonContext.Default.SampleMessage, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal("ping", read!.Type);
        Assert.Equal("hello", read.Value);
    }

    [Fact]
    public async Task RunAsync_Ping_ReturnsPong()
    {
        var input = new MemoryStream();
        await NativeMessageProtocol.WriteAsync(input, new NativeRequest("req-1", "ping"), NativeMessageJsonContext.Default.NativeRequest, TestContext.Current.CancellationToken);
        input.Position = 0;
        var output = new MemoryStream();

        var exitCode = await NativeMessagingHost.RunAsync(input, output, TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        output.Position = 0;
        var response = await NativeMessageProtocol.ReadAsync(output, NativeMessageJsonContext.Default.NativeResponse, TestContext.Current.CancellationToken);
        Assert.NotNull(response);
        Assert.Equal("req-1", response!.Id);
        Assert.Equal("pong", response.Type);
        Assert.True(response.Ok);
        var payload = Assert.IsType<JsonElement>(response.Payload);
        Assert.Equal(NativeMessagingHost.HostName, payload.GetProperty("name").GetString());
    }

    [Fact]
    public async Task HandleAsync_PasskeyGet_ForwardsToAppBridge()
    {
        var assertionPayload = new PasskeyGetAssertionPayload("AQID", "auth", "client", "sig", "BQYH");
        var bridge = new FakeAppPasskeyBridgeClient
        {
            Response = new BrowserPasskeyResponse(
                "req-2",
                "passkey.get",
                true,
                JsonSerializer.SerializeToElement(assertionPayload, PasskeyJsonContext.Default.PasskeyGetAssertionPayload)),
        };

        var response = await NativeMessagingHost.HandleAsync(new NativeRequest("req-2", "passkey.get", JsonDocument.Parse(
            """
            {
              "origin": "https://github.com",
              "rpId": "github.com",
              "challenge": "AQIDBA",
              "allowCredentials": [],
              "userVerification": "preferred",
              "mediation": null,
              "timeout": 60000
            }
            """).RootElement), bridge, TestContext.Current.CancellationToken);

        Assert.True(response.Ok);
        Assert.Equal("passkey.get", response.Type);
        Assert.NotNull(bridge.Request);
        Assert.Equal("req-2", bridge.Request!.Id);
        Assert.Equal("passkey.get", bridge.Request.Type);
    }

    [Fact]
    public async Task HandleAsync_PasskeyCreate_ReturnsNotImplemented()
    {
        var response = await NativeMessagingHost.HandleAsync(new NativeRequest("req-create", "passkey.create", JsonDocument.Parse(
            """
            {
              "origin": "https://github.com",
              "rpId": "github.com",
              "attestation": "none",
              "challenge": "AQIDBA",
              "excludeCredentials": [],
              "pubKeyCredParams": [{ "type": "public-key", "alg": -7 }],
              "rp": { "id": "github.com", "name": "GitHub" },
              "user": { "id": "BQYH", "name": "octo@example.com", "displayName": "Octo" },
              "timeout": 60000
            }
            """).RootElement), new FakeAppPasskeyBridgeClient(), TestContext.Current.CancellationToken);

        Assert.False(response.Ok);
        Assert.Equal("error", response.Type);
        Assert.Equal("not_implemented", response.Error?.Code);
    }

    [Fact]
    public async Task HandleAsync_PasskeyRequests_RejectsInvalidPayloadBeforeForwarding()
    {
        var bridge = new FakeAppPasskeyBridgeClient();

        var response = await NativeMessagingHost.HandleAsync(new NativeRequest("req-3", "passkey.get", JsonDocument.Parse(
            """
            {
              "origin": "not an origin",
              "challenge": "AQIDBA",
              "allowCredentials": []
            }
            """).RootElement), bridge, TestContext.Current.CancellationToken);

        Assert.False(response.Ok);
        Assert.Equal("error", response.Type);
        Assert.Equal("invalid_request", response.Error?.Code);
        Assert.Null(bridge.Request);
    }

    [Fact]
    public void PasskeyRequestParser_ReadsCreatePayload()
    {
        var request = new NativeRequest("req-4", "passkey.create", JsonDocument.Parse(
            """
            {
              "origin": "https://example.com",
              "rpId": "example.com",
              "attestation": "none",
              "challenge": "AQIDBA",
              "excludeCredentials": [{ "id": "BQYH", "type": "public-key", "transports": ["internal"] }],
              "pubKeyCredParams": [{ "type": "public-key", "alg": -7 }],
              "rp": { "id": "example.com", "name": "Example" },
              "user": { "id": "CAkK", "name": "octo@example.com", "displayName": "Octo" },
              "timeout": 60000
            }
            """).RootElement);

        var parsed = PasskeyRequestParser.TryParse(
            new BrowserPasskeyRequest(request.Id, request.Type, request.Payload),
            out var result,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        var payload = Assert.IsType<PasskeyCreatePayload>(result!.Payload);
        Assert.Equal("https://example.com", payload.Origin);
        Assert.Equal("example.com", payload.RpId);
        Assert.Equal("AQIDBA", payload.Challenge);
        Assert.Equal(-7, Assert.Single(payload.PubKeyCredParams).Alg);
        Assert.Equal("CAkK", payload.User!.Id);
        Assert.Equal("BQYH", Assert.Single(payload.ExcludeCredentials).Id);
    }

    private sealed class FakeAppPasskeyBridgeClient : IAppPasskeyBridgeClient
    {
        public BrowserPasskeyRequest? Request { get; private set; }
        public BrowserPasskeyResponse Response { get; init; } = new(
            "req",
            "error",
            false,
            Error: new BrowserPasskeyError("not_set", "not set"));

        public Task<BrowserPasskeyResponse> SendAsync(BrowserPasskeyRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(Response);
        }
    }
}
