using System.Net;
using System.Text;
using System.Text.Json;
using Api.Dtos;
using Core.Models;
using Xunit;

namespace Api.Tests;

public sealed class WebAuthnTwoFactorWireTests
{
    [Fact]
    public void VerificationFactories_WriteOnlyTheSelectedField()
    {
        var cases = new[]
        {
            (WebAuthnVerificationRequest.FromMasterPasswordHash("master-hash"), "masterPasswordHash", "master-hash"),
            (WebAuthnVerificationRequest.FromOtp("123456"), "otp", "123456"),
            (WebAuthnVerificationRequest.FromAuthRequestAccessCode("access-code"), "authRequestAccessCode", "access-code"),
            (WebAuthnVerificationRequest.FromUserVerificationToken("uv-token"), "userVerificationToken", "uv-token"),
        };

        foreach (var (request, fieldName, expectedValue) in cases)
        {
            using var json = JsonDocument.Parse(
                JsonSerializer.Serialize(request, ApiJsonContext.Default.WebAuthnVerificationRequest));
            var property = Assert.Single(json.RootElement.EnumerateObject());
            Assert.Equal(fieldName, property.Name);
            Assert.Equal(expectedValue, property.Value.GetString());
        }
    }

    [Fact]
    public void VerificationRequest_RejectsMissingOrAmbiguousSelections()
    {
        Assert.Throws<ArgumentException>(() => new WebAuthnVerificationRequest(null, null, null, null));
        Assert.Throws<ArgumentException>(() => new WebAuthnVerificationRequest(
            "master-hash",
            null,
            null,
            "uv-token"));
        Assert.Throws<ArgumentException>(() => new WebAuthnVerificationRequest(
            "master-hash",
            " ",
            null,
            null));
    }

    [Fact]
    public void ProviderResponse_ReadsVaultwardenFlatShape()
    {
        const string payload =
            """{"enabled":true,"keys":[{"id":1,"name":"Security key","migrated":false}],"object":"twoFactorWebAuthn"}""";

        var response = JsonSerializer.Deserialize(
            payload,
            ApiJsonContext.Default.WebAuthnTwoFactorResponse)!;

        Assert.True(response.Enabled);
        var key = Assert.Single(response.Keys);
        Assert.Equal(1, key.Id);
        Assert.Equal("Security key", key.Name);
        Assert.False(key.Migrated);
        Assert.Equal("twoFactorWebAuthn", response.Object);
        Assert.Null(response.UserVerificationToken);
    }

    [Fact]
    public void ProviderResponse_ReadsOfficialWrapperAndStringCredentialId()
    {
        const string payload =
            """{"WebAuthn":{"Enabled":true,"Keys":[{"Id":"2","Name":"Platform key","Migrated":true}]},"UserVerificationToken":"uv-token"}""";

        var response = JsonSerializer.Deserialize(
            payload,
            ApiJsonContext.Default.WebAuthnTwoFactorResponse)!;

        Assert.True(response.Enabled);
        var key = Assert.Single(response.Keys);
        Assert.Equal(2, key.Id);
        Assert.Equal("Platform key", key.Name);
        Assert.True(key.Migrated);
        Assert.Equal("uv-token", response.UserVerificationToken);
    }

    [Fact]
    public void ProviderResponse_AllowsOfficialNullListAndUnnamedCredential()
    {
        const string unnamedPayload =
            """{"WebAuthn":{"Enabled":true,"Keys":[{"Id":2,"Name":null,"Migrated":false}]},"UserVerificationToken":"uv-token"}""";
        const string emptyPayload =
            """{"WebAuthn":{"Enabled":false,"Keys":null},"UserVerificationToken":"uv-token"}""";
        const string omittedPayload =
            """{"WebAuthn":{"Enabled":false},"UserVerificationToken":"uv-token"}""";

        var unnamed = JsonSerializer.Deserialize(
            unnamedPayload,
            ApiJsonContext.Default.WebAuthnTwoFactorResponse)!;
        var empty = JsonSerializer.Deserialize(
            emptyPayload,
            ApiJsonContext.Default.WebAuthnTwoFactorResponse)!;
        var omitted = JsonSerializer.Deserialize(
            omittedPayload,
            ApiJsonContext.Default.WebAuthnTwoFactorResponse)!;

        Assert.Equal(string.Empty, Assert.Single(unnamed.Keys).Name);
        Assert.Empty(empty.Keys);
        Assert.Empty(omitted.Keys);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"enabled\":true,\"keys\":[null]}")]
    [InlineData("{\"enabled\":true,\"keys\":[{\"id\":{},\"name\":\"key\"}]}")]
    [InlineData("{\"enabled\":true,\"keys\":\"hidden\"}")]
    [InlineData("{\"enabled\":true}")]
    [InlineData("{\"webAuthn\":null}")]
    public void ProviderResponse_RejectsMalformedShapes(string payload)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            payload,
            ApiJsonContext.Default.WebAuthnTwoFactorResponse));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChallengeResponse_PreservesFlatAndWrappedOptions(bool wrapped)
    {
        var payload = wrapped
            ? """{"Options":{"challenge":"abc","rp":{"id":"vault.example"},"excludeCredentials":[]}}"""
            : """{"challenge":"abc","rp":{"id":"vault.example"},"excludeCredentials":[],"status":"ok"}""";

        var response = JsonSerializer.Deserialize(
            payload,
            ApiJsonContext.Default.WebAuthnChallengeResponse)!;

        Assert.NotNull(response.Options);
        Assert.Equal("abc", response.Options.Value.GetProperty("challenge").GetString());
        Assert.Equal("vault.example", response.Options.Value.GetProperty("rp").GetProperty("id").GetString());
        if (!wrapped)
            Assert.Equal("ok", response.Options.Value.GetProperty("status").GetString());
    }

    [Fact]
    public void EnableRequest_WritesVaultwardenAttestationContract()
    {
        var request = CreateEnableRequest();

        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(request, ApiJsonContext.Default.EnableWebAuthnRequest));
        var device = json.RootElement.GetProperty("deviceResponse");
        var response = device.GetProperty("response");

        Assert.Equal(3, json.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("Security key", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("credential", device.GetProperty("id").GetString());
        Assert.Equal("raw-id", device.GetProperty("rawId").GetString());
        Assert.Equal("public-key", device.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, device.GetProperty("extensions").ValueKind);
        Assert.Empty(device.GetProperty("extensions").EnumerateObject());
        Assert.Equal("attestation", response.GetProperty("AttestationObject").GetString());
        Assert.Equal("client-data", response.GetProperty("clientDataJson").GetString());
        Assert.Empty(response.GetProperty("transports").EnumerateArray());
        Assert.Equal("master-hash", json.RootElement.GetProperty("masterPasswordHash").GetString());
        Assert.False(json.RootElement.TryGetProperty("otp", out _));
        Assert.False(json.RootElement.TryGetProperty("authRequestAccessCode", out _));
        Assert.False(json.RootElement.TryGetProperty("userVerificationToken", out _));
    }

    [Fact]
    public void EnableRequest_CanWriteOnlyOfficialVerificationToken()
    {
        var request = CreateEnableRequest(
            WebAuthnVerificationRequest.FromUserVerificationToken("uv-token"));

        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(request, ApiJsonContext.Default.EnableWebAuthnRequest));

        Assert.Equal("uv-token", json.RootElement.GetProperty("userVerificationToken").GetString());
        Assert.False(json.RootElement.TryGetProperty("masterPasswordHash", out _));
        Assert.False(json.RootElement.TryGetProperty("otp", out _));
        Assert.False(json.RootElement.TryGetProperty("authRequestAccessCode", out _));
    }

    [Fact]
    public void DeleteRequest_WritesCredentialIdAndOnlySelectedVerificationField()
    {
        var vaultwarden = DeleteWebAuthnCredentialRequest.Create(
            4,
            WebAuthnVerificationRequest.FromMasterPasswordHash("master-hash"));
        var official = DeleteWebAuthnCredentialRequest.Create(
            4,
            WebAuthnVerificationRequest.FromUserVerificationToken("uv-token"));

        Assert.Equal(
            """{"id":4,"masterPasswordHash":"master-hash"}""",
            JsonSerializer.Serialize(vaultwarden, ApiJsonContext.Default.DeleteWebAuthnCredentialRequest));
        Assert.Equal(
            """{"id":4,"userVerificationToken":"uv-token"}""",
            JsonSerializer.Serialize(official, ApiJsonContext.Default.DeleteWebAuthnCredentialRequest));
    }

    [Fact]
    public void DeleteAllRequest_WritesOnlyOfficialVerificationToken()
    {
        var request = DeleteAllWebAuthnCredentialsRequest.FromUserVerificationToken("uv-token");

        Assert.Equal(
            """{"userVerificationToken":"uv-token"}""",
            JsonSerializer.Serialize(request, ApiJsonContext.Default.DeleteAllWebAuthnCredentialsRequest));
        Assert.Throws<ArgumentException>(() => new DeleteAllWebAuthnCredentialsRequest(" "));
    }

    [Fact]
    public async Task ManagementEndpoints_UseExpectedMethodsPathsAndBodies()
    {
        var transport = new RecordingHandler();
        using var api = new ApiClient(
            new HttpClient(transport),
            ServerEndpoints.ForSelfHosted("https://vault.example/warden"));
        var client = Assert.IsAssignableFrom<ITwoFactorApiClient>((IAuthenticatedApiClient)api);

        var details = await client.GetWebAuthnAsync(
            WebAuthnVerificationRequest.FromMasterPasswordHash("master-hash"),
            TestContext.Current.CancellationToken);
        var challenge = await client.GetWebAuthnChallengeAsync(
            WebAuthnVerificationRequest.FromUserVerificationToken("uv-token"),
            TestContext.Current.CancellationToken);
        var enabled = await client.EnableWebAuthnAsync(
            CreateEnableRequest(WebAuthnVerificationRequest.FromUserVerificationToken("uv-token")),
            TestContext.Current.CancellationToken);
        var deleted = await client.DeleteWebAuthnCredentialAsync(
            DeleteWebAuthnCredentialRequest.Create(
                3,
                WebAuthnVerificationRequest.FromUserVerificationToken("uv-token")),
            TestContext.Current.CancellationToken);
        await client.DeleteAllWebAuthnCredentialsAsync(
            DeleteAllWebAuthnCredentialsRequest.FromUserVerificationToken("uv-token"),
            TestContext.Current.CancellationToken);
        await client.RequestProtectedActionOtpAsync(TestContext.Current.CancellationToken);

        Assert.True(details.Enabled);
        Assert.Equal("challenge", challenge.Options!.Value.GetProperty("challenge").GetString());
        Assert.Equal("official-token", enabled.UserVerificationToken);
        Assert.Empty(deleted.Keys);
        Assert.Collection(
            transport.Requests,
            request => AssertRequest(
                request,
                HttpMethod.Post,
                "/warden/api/two-factor/get-webauthn"),
            request => AssertRequest(
                request,
                HttpMethod.Post,
                "/warden/api/two-factor/get-webauthn-challenge"),
            request => AssertRequest(
                request,
                HttpMethod.Put,
                "/warden/api/two-factor/webauthn"),
            request => AssertRequest(
                request,
                HttpMethod.Delete,
                "/warden/api/two-factor/webauthn"),
            request => AssertRequest(
                request,
                HttpMethod.Delete,
                "/warden/api/two-factor/webauthn/all"),
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/warden/api/accounts/request-otp", request.Uri.AbsolutePath);
                Assert.Null(request.ContentType);
                Assert.Null(request.Body);
            });

        using var getBody = JsonDocument.Parse(transport.Requests[0].Body!);
        Assert.Equal("master-hash", getBody.RootElement.GetProperty("masterPasswordHash").GetString());
        Assert.Single(getBody.RootElement.EnumerateObject());
        using var challengeBody = JsonDocument.Parse(transport.Requests[1].Body!);
        Assert.Equal("uv-token", challengeBody.RootElement.GetProperty("userVerificationToken").GetString());
        Assert.Single(challengeBody.RootElement.EnumerateObject());
        using var deleteBody = JsonDocument.Parse(transport.Requests[3].Body!);
        Assert.Equal(3, deleteBody.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("uv-token", deleteBody.RootElement.GetProperty("userVerificationToken").GetString());
        Assert.Equal(2, deleteBody.RootElement.EnumerateObject().Count());
        Assert.Equal(
            """{"userVerificationToken":"uv-token"}""",
            transport.Requests[4].Body);
    }

    private static void AssertRequest(
        RecordedRequest request,
        HttpMethod method,
        string absolutePath)
    {
        Assert.Equal(method, request.Method);
        Assert.Equal(absolutePath, request.Uri.AbsolutePath);
        Assert.Equal("application/json", request.ContentType);
    }

    private static EnableWebAuthnRequest CreateEnableRequest(
        WebAuthnVerificationRequest? verification = null) =>
        EnableWebAuthnRequest.Create(
            3,
            "Security key",
            new WebAuthnRegistrationCredential(
                "credential",
                "raw-id",
                new WebAuthnAttestationResponse("attestation", "client-data", [])),
            verification ?? WebAuthnVerificationRequest.FromMasterPasswordHash("master-hash"));

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? ContentType,
        string? Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Content?.Headers.ContentType?.MediaType,
                body));

            var payload = request.RequestUri!.AbsolutePath.EndsWith(
                "/get-webauthn-challenge",
                StringComparison.Ordinal)
                    ? """{"challenge":"challenge","rp":{"id":"vault.example"}}"""
                    : request.Method == HttpMethod.Put
                        ? """{"webAuthn":{"enabled":true,"keys":[{"id":3,"name":"Security key","migrated":false}]},"userVerificationToken":"official-token"}"""
                        : request.Method == HttpMethod.Delete
                            ? """{"enabled":true,"keys":[],"object":"twoFactorU2f"}"""
                            : """{"enabled":true,"keys":[{"id":1,"name":"Existing key","migrated":false}],"object":"twoFactorWebAuthn"}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }
}
