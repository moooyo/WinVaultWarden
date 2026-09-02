using System.Net;
using System.Text;
using System.Text.Json;
using Api.Dtos;
using Core.Models;
using Xunit;

namespace Api.Tests;

public sealed class TdeWireTests
{
    [Fact]
    public void TrustedDeviceOption_UsesExactPascalCaseWireContract()
    {
        const string tokenJson = """
        {"access_token":"at","refresh_token":"rt","expires_in":3600,"Kdf":0,"KdfIterations":600000,
         "UserDecryptionOptions":{"HasMasterPassword":false,"TrustedDeviceOption":{
           "HasAdminApproval":true,"HasLoginApprovingDevice":false,
           "HasManageResetPasswordPermission":true,"IsTdeOffboarding":false,
           "EncryptedPrivateKey":"2.private","EncryptedUserKey":"4.user"}}}
        """;

        var token = JsonSerializer.Deserialize(tokenJson, ApiJsonContext.Default.TokenResponse)!;
        var option = Assert.IsType<TrustedDeviceUserDecryptionOptionResponse>(
            token.UserDecryptionOptions!.TrustedDeviceOption);

        Assert.True(option.HasAdminApproval);
        Assert.False(option.HasLoginApprovingDevice);
        Assert.True(option.HasManageResetPasswordPermission);
        Assert.False(option.IsTdeOffboarding);
        Assert.Equal("2.private", option.EncryptedPrivateKey);
        Assert.Equal("4.user", option.EncryptedUserKey);

        Assert.Equal(
            """{"HasAdminApproval":true,"HasLoginApprovingDevice":false,"HasManageResetPasswordPermission":true,"IsTdeOffboarding":false,"EncryptedPrivateKey":"2.private","EncryptedUserKey":"4.user"}""",
            JsonSerializer.Serialize(
                option,
                ApiJsonContext.Default.TrustedDeviceUserDecryptionOptionResponse));
    }

    [Fact]
    public void TrustedDeviceOption_MissingFlagsRemainUnknown()
    {
        const string tokenJson = """
        {"access_token":"at","refresh_token":"rt","expires_in":3600,"Kdf":0,"KdfIterations":600000,
         "UserDecryptionOptions":{"HasMasterPassword":false,"TrustedDeviceOption":{}}}
        """;

        var token = JsonSerializer.Deserialize(
            tokenJson,
            ApiJsonContext.Default.TokenResponse)!;
        var option = Assert.IsType<TrustedDeviceUserDecryptionOptionResponse>(
            token.UserDecryptionOptions!.TrustedDeviceOption);

        Assert.Null(option.HasAdminApproval);
        Assert.Null(option.HasLoginApprovingDevice);
        Assert.Null(option.HasManageResetPasswordPermission);
        Assert.Null(option.IsTdeOffboarding);
    }

    [Fact]
    public void DeviceDto_RoundTripsOfficialOptionalFieldsWithExactCasing()
    {
        const string json = """
        {"Id":"device-id","Name":"Desktop","Type":6,"Identifier":"device-identifier",
         "CreationDate":"2026-08-01T12:00:00Z","IsTrusted":true,"UserId":"user-id",
         "RevisionDate":"2026-08-02T12:00:00Z","EncryptedUserKey":"4.user",
         "EncryptedPublicKey":"2.public","DevicePendingAuthRequest":{
           "Id":"request-id","CreationDate":"2026-08-03T12:00:00Z"},
         "LastActivityDate":"2026-08-04T12:00:00Z"}
        """;

        var device = JsonSerializer.Deserialize(json, ApiJsonContext.Default.DeviceDto)!;

        Assert.Equal("user-id", device.UserId);
        Assert.Equal("4.user", device.EncryptedUserKey);
        Assert.Equal("2.public", device.EncryptedPublicKey);
        Assert.Equal("request-id", device.DevicePendingAuthRequest!.Id);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
            device.LastActivityDate);

        using var serialized = JsonDocument.Parse(
            JsonSerializer.Serialize(device, ApiJsonContext.Default.DeviceDto));
        Assert.Equal(
            [
                "id", "name", "type", "identifier", "creationDate", "isTrusted",
                "userId", "revisionDate", "encryptedUserKey", "encryptedPublicKey",
                "devicePendingAuthRequest", "lastActivityDate",
            ],
            serialized.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void TrustedDeviceKeysRequest_UsesExactCamelCaseWireContract()
    {
        var request = new TrustedDeviceKeysRequest(
            "4.user",
            "2.public",
            "2.private");

        Assert.Equal(
            """{"encryptedUserKey":"4.user","encryptedPublicKey":"2.public","encryptedPrivateKey":"2.private"}""",
            JsonSerializer.Serialize(request, ApiJsonContext.Default.TrustedDeviceKeysRequest));
    }

    [Fact]
    public async Task UpdateTrustedDeviceKeys_UsesExpectedPathAndRequestBody()
    {
        var transport = new RecordingHandler();
        using var api = new ApiClient(
            new HttpClient(transport),
            ServerEndpoints.ForSelfHosted("https://vault.example/warden"));
        var client = Assert.IsAssignableFrom<IDeviceTrustApiClient>(
            (IAuthenticatedApiClient)api);

        var result = await client.UpdateTrustedDeviceKeysAsync(
            "device-123",
            new TrustedDeviceKeysRequest("4.user", "2.public", "2.private"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, transport.Method);
        Assert.Equal(
            "/warden/api/devices/device-123/keys",
            transport.RequestUri!.AbsolutePath);
        Assert.Equal("application/json", transport.ContentType);
        Assert.Equal(
            """{"encryptedUserKey":"4.user","encryptedPublicKey":"2.public","encryptedPrivateKey":"2.private"}""",
            transport.Body);
        Assert.True(result.IsTrusted);
        Assert.Equal("4.user", result.EncryptedUserKey);
        Assert.Equal("2.public", result.EncryptedPublicKey);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? ContentType { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"device-id","name":"Desktop","type":6,"identifier":"device-123","creationDate":"2026-08-01T12:00:00Z","isTrusted":true,"encryptedUserKey":"4.user","encryptedPublicKey":"2.public"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
