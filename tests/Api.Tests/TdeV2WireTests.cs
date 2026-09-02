using System.Net;
using System.Text;
using System.Text.Json;
using Api.Dtos;
using Core.Models;
using Xunit;

namespace Api.Tests;

public sealed class TdeV2WireTests
{
    [Fact]
    public void IdentityAccountKeys_UsesPascalOuterAndCamelNestedContract()
    {
        const string json = """
        {"access_token":"at","refresh_token":"rt","expires_in":3600,"Kdf":0,"KdfIterations":600000,
         "AccountKeys":{"publicKeyEncryptionKeyPair":{"wrappedPrivateKey":"7.private","publicKey":"spki","signedPublicKey":"signed"},
         "signatureKeyPair":{"wrappedSigningKey":"7.signing","verifyingKey":"cose-key"},
         "securityState":{"securityState":"cose-sign1","securityVersion":2}}}
        """;

        var token = JsonSerializer.Deserialize(json, ApiJsonContext.Default.TokenResponse)!;

        Assert.Equal("7.private", token.AccountKeys!.PublicKeyEncryptionKeyPair!.WrappedPrivateKey);
        Assert.Equal("7.signing", token.AccountKeys.SignatureKeyPair!.WrappedSigningKey);
        Assert.Equal(2, token.AccountKeys.SecurityState!.SecurityVersion);
    }

    [Fact]
    public void IdentityAccountKeys_AllowsTransitionalPublicFieldsToBeOmitted()
    {
        const string json = """
        {"access_token":"at","refresh_token":"rt","expires_in":3600,"Kdf":0,"KdfIterations":600000,
         "AccountKeys":{"publicKeyEncryptionKeyPair":{"wrappedPrivateKey":"7.private"},
         "signatureKeyPair":{"wrappedSigningKey":"7.signing"},
         "securityState":{"securityState":"cose-sign1","securityVersion":2}}}
        """;

        var token = JsonSerializer.Deserialize(json, ApiJsonContext.Default.TokenResponse)!;

        Assert.Equal("7.private", token.AccountKeys!.PublicKeyEncryptionKeyPair!.WrappedPrivateKey);
        Assert.Null(token.AccountKeys.PublicKeyEncryptionKeyPair.PublicKey);
        Assert.Null(token.AccountKeys.PublicKeyEncryptionKeyPair.SignedPublicKey);
        Assert.Equal("7.signing", token.AccountKeys.SignatureKeyPair!.WrappedSigningKey);
        Assert.Null(token.AccountKeys.SignatureKeyPair.VerifyingKey);
        Assert.Equal(2, token.AccountKeys.SecurityState!.SecurityVersion);
    }

    [Fact]
    public void PostAccountKeysRequest_UsesOfficialV2ShapeAndFieldOrder()
    {
        var request = CreateRequest();

        Assert.Equal(
            """{"publicKey":"spki","encryptedPrivateKey":"7.private","accountKeys":{"userKeyEncryptedAccountPrivateKey":"7.private","accountPublicKey":"spki","publicKeyEncryptionKeyPair":{"wrappedPrivateKey":"7.private","publicKey":"spki","signedPublicKey":"signed"},"signatureKeyPair":{"signatureAlgorithm":"mldsa44","wrappedSigningKey":"7.signing","verifyingKey":"cose-key"},"securityState":{"securityState":"cose-sign1","securityVersion":2}},"userKeyId":"00112233445566778899aabbccddeeff"}""",
            JsonSerializer.Serialize(
                request,
                ApiJsonContext.Default.PostAccountKeysRequest));
    }

    [Fact]
    public async Task TdeRegistrationEndpoints_UseOfficialPathsAndBodies()
    {
        var transport = new RecordingHandler();
        using var api = new ApiClient(
            new HttpClient(transport),
            ServerEndpoints.ForSelfHosted("https://vault.example/warden"));
        var client = Assert.IsAssignableFrom<ITdeRegistrationApiClient>(
            (IAuthenticatedApiClient)api);

        await client.PostAccountKeysAsync(
            CreateRequest(),
            TestContext.Current.CancellationToken);
        await client.EnrollResetPasswordAsync(
            "11111111-2222-3333-4444-555555555555",
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            new ResetPasswordEnrollmentRequest("4.reset", null),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["/warden/api/accounts/keys", "/warden/api/organizations/11111111-2222-3333-4444-555555555555/users/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/reset-password-enrollment"],
            transport.Requests.Select(request => request.Path).ToArray());
        Assert.Equal([HttpMethod.Post, HttpMethod.Put], transport.Requests.Select(request => request.Method).ToArray());
        Assert.Equal(
            """{"resetPasswordKey":"4.reset","masterPasswordHash":null}""",
            transport.Requests[1].Body);
    }

    [Fact]
    public async Task AdministratorApproval_UsesAuthenticatedAdminRequestPathAndTypeTwo()
    {
        var transport = new RecordingHandler();
        using var api = new ApiClient(
            new HttpClient(transport),
            ServerEndpoints.ForSelfHosted("https://vault.example/warden"));
        var client = Assert.IsAssignableFrom<IAuthRequestApiClient>(
            (IAuthenticatedApiClient)api);

        await client.CreateAdminAsync(
            new AuthRequestRequest(
                "access-code",
                "11111111-2222-3333-4444-555555555555",
                "me@example.com",
                "public-key",
                2),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(transport.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/warden/api/auth-requests/admin-request", request.Path);
        Assert.Equal(
            """{"accessCode":"access-code","deviceIdentifier":"11111111-2222-3333-4444-555555555555","email":"me@example.com","publicKey":"public-key","type":2}""",
            request.Body);
    }

    [Fact]
    public async Task JitContextEndpoints_UseFrozenIdentifierAndCanonicalOrganizationId()
    {
        var transport = new RecordingHandler();
        using var api = new ApiClient(
            new HttpClient(transport),
            ServerEndpoints.ForSelfHosted("https://vault.example/warden"));
        var client = Assert.IsAssignableFrom<ITdeRegistrationApiClient>(
            (IAuthenticatedApiClient)api);

        var status = await client.GetAutoEnrollStatusAsync(
            "enterprise sso",
            TestContext.Current.CancellationToken);
        var keys = await client.GetOrganizationKeysAsync(
            "11111111-2222-3333-4444-555555555555",
            TestContext.Current.CancellationToken);

        Assert.True(status.ResetPasswordEnabled);
        Assert.Equal("organization-spki", keys.PublicKey);
        Assert.Equal(
            [
                "/warden/api/organizations/enterprise%20sso/auto-enroll-status",
                "/warden/api/organizations/11111111-2222-3333-4444-555555555555/keys",
            ],
            transport.Requests.Select(request => request.Path).ToArray());
    }

    private static PostAccountKeysRequest CreateRequest() => new(
        "spki",
        "7.private",
        new AccountKeysRequest(
            "7.private",
            "spki",
            new PublicKeyEncryptionKeyPairRequest("7.private", "spki", "signed"),
            new SignatureKeyPairRequest("mldsa44", "7.signing", "cose-key"),
            new AccountSecurityState("cose-sign1", 2)),
        "00112233445566778899aabbccddeeff");

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));

            var isAdminRequest = request.RequestUri.AbsolutePath.EndsWith(
                "/api/auth-requests/admin-request",
                StringComparison.Ordinal);
            var isAutoEnroll = request.RequestUri.AbsolutePath.EndsWith(
                "/auto-enroll-status",
                StringComparison.Ordinal);
            var isOrganizationKeys = request.Method == HttpMethod.Get
                && request.RequestUri.AbsolutePath.EndsWith("/keys", StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = isAutoEnroll
                    ? new StringContent(
                        """{"id":"11111111-2222-3333-4444-555555555555","resetPasswordEnabled":true,"object":"organizationAutoEnrollStatus"}""",
                        Encoding.UTF8,
                        "application/json")
                    : isOrganizationKeys
                    ? new StringContent(
                        """{"publicKey":"organization-spki","privateKey":null,"object":"organizationKeys"}""",
                        Encoding.UTF8,
                        "application/json")
                    : isAdminRequest
                    ? new StringContent(
                        """{"id":"request-id","publicKey":"public-key","requestDeviceType":"Windows","requestIpAddress":"127.0.0.1","key":null,"masterPasswordHash":null,"creationDate":"2026-08-31T00:00:00Z","responseDate":null,"requestApproved":null,"origin":null}""",
                        Encoding.UTF8,
                        "application/json")
                    : request.Method == HttpMethod.Post
                    ? new StringContent(
                        """{"object":"keys","key":null,"publicKey":"spki","privateKey":"7.private","accountKeys":null}""",
                        Encoding.UTF8,
                        "application/json")
                    : new StringContent(string.Empty),
            };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Body);
}
