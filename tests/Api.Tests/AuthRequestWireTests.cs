using System.Net;
using System.Text;
using System.Text.Json;
using Api;
using Api.Dtos;
using Xunit;

namespace Api.Tests;

/// <summary>
/// 断言 auth-request DTOs 序列化时字段名与 Vaultwarden API 约定完全一致（camelCase）。
/// 对应端点：GET/POST/PUT /api/auth-requests[/{id}]
/// </summary>
public class AuthRequestWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ── AuthResponseRequest ─────────────────────────────────────────────────

    [Fact]
    public void AuthResponseRequest_serialises_deviceIdentifier_key_masterPasswordHash_requestApproved()
    {
        var req = new AuthResponseRequest("dev-123", "enc-key-xyz", "hash-abc", true);
        var json = JsonSerializer.Serialize(req, Web);
        Assert.Contains("\"deviceIdentifier\":\"dev-123\"", json);
        Assert.Contains("\"key\":\"enc-key-xyz\"", json);
        Assert.Contains("\"masterPasswordHash\":\"hash-abc\"", json);
        Assert.Contains("\"requestApproved\":true", json);
    }

    [Fact]
    public void AuthResponseRequest_nullable_masterPasswordHash_serialises_null()
    {
        var req = new AuthResponseRequest("dev-123", "enc-key-xyz", null, false);
        var json = JsonSerializer.Serialize(req, Web);
        Assert.Contains("\"masterPasswordHash\":null", json);
        Assert.Contains("\"requestApproved\":false", json);
    }

    [Fact]
    public void AuthResponseRequest_context_matches_reflection()
    {
        var req = new AuthResponseRequest("dev-id", "the-key", "hash", true);
        Assert.Equal(
            JsonSerializer.Serialize(req, Web),
            JsonSerializer.Serialize(req, ApiJsonContext.Default.AuthResponseRequest));
    }

    // ── AuthRequestRequest ──────────────────────────────────────────────────

    [Fact]
    public void AuthRequestRequest_serialises_accessCode_deviceIdentifier_email_publicKey()
    {
        var req = new AuthRequestRequest("code-abc", "dev-123", "user@example.com", "pub-key-xyz", 0);
        var json = JsonSerializer.Serialize(req, Web);
        Assert.Contains("\"accessCode\":\"code-abc\"", json);
        Assert.Contains("\"deviceIdentifier\":\"dev-123\"", json);
        Assert.Contains("\"email\":\"user@example.com\"", json);
        Assert.Contains("\"publicKey\":\"pub-key-xyz\"", json);
        Assert.Contains("\"type\":0", json);
    }

    [Fact]
    public void AuthRequestRequest_context_matches_reflection()
    {
        var req = new AuthRequestRequest("code", "dev-id", "user@test.com", "pub-key", 0);
        Assert.Equal(
            JsonSerializer.Serialize(req, Web),
            JsonSerializer.Serialize(req, ApiJsonContext.Default.AuthRequestRequest));
    }

    // ── AuthRequestResponse ─────────────────────────────────────────────────

    [Fact]
    public void AuthRequestResponse_serialises_requestDeviceType_requestIpAddress_requestApproved()
    {
        var resp = new AuthRequestResponse(
            "id-001",
            "pub-key",
            "Windows",
            "192.168.1.1",
            "enc-key",
            "hash-abc",
            "2025-01-01T00:00:00Z",
            "2025-01-01T00:01:00Z",
            true,
            "https://app.bitwarden.com");
        var json = JsonSerializer.Serialize(resp, Web);
        Assert.Contains("\"requestDeviceType\":\"Windows\"", json);
        Assert.Contains("\"requestIpAddress\":\"192.168.1.1\"", json);
        Assert.Contains("\"requestApproved\":true", json);
        Assert.Contains("\"creationDate\":\"2025-01-01T00:00:00Z\"", json);
    }

    [Fact]
    public void AuthRequestResponse_nullable_fields_serialise_null()
    {
        var resp = new AuthRequestResponse(
            "id-001",
            "pub-key",
            "Unknown",
            "10.0.0.1",
            null,
            null,
            "2025-06-01T10:00:00Z",
            null,
            null,
            null);
        var json = JsonSerializer.Serialize(resp, Web);
        Assert.Contains("\"key\":null", json);
        Assert.Contains("\"masterPasswordHash\":null", json);
        Assert.Contains("\"responseDate\":null", json);
        Assert.Contains("\"requestApproved\":null", json);
        Assert.Contains("\"origin\":null", json);
    }

    [Fact]
    public void AuthRequestResponse_context_matches_reflection()
    {
        var resp = new AuthRequestResponse(
            "id-001",
            "pub-key",
            "Windows",
            "192.168.1.1",
            "enc-key",
            "hash-abc",
            "2025-01-01T00:00:00Z",
            "2025-01-01T00:01:00Z",
            true,
            "https://app.bitwarden.com");
        Assert.Equal(
            JsonSerializer.Serialize(resp, Web),
            JsonSerializer.Serialize(resp, ApiJsonContext.Default.AuthRequestResponse));
    }

    [Fact]
    public void AuthRequestResponse_reads_official_optional_device_metadata()
    {
        const string json =
            """{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","publicKey":"public-key","requestDeviceType":"Windows Desktop","requestDeviceTypeValue":6,"requestDeviceIdentifier":"11111111-2222-3333-4444-555555555555","requestIpAddress":"127.0.0.1","requestCountryName":"United States","requestDeviceId":"99999999-8888-7777-6666-555555555555","key":null,"masterPasswordHash":null,"creationDate":"2026-08-30T00:00:00Z","responseDate":null,"requestApproved":false,"origin":"vault.bitwarden.com","object":"auth-request"}""";

        var response = JsonSerializer.Deserialize(
            json,
            ApiJsonContext.Default.AuthRequestResponse)!;

        Assert.Equal("11111111-2222-3333-4444-555555555555", response.RequestDeviceIdentifier);
        Assert.Equal(6, response.RequestDeviceTypeValue);
        Assert.Equal("United States", response.RequestCountryName);
        Assert.Equal("99999999-8888-7777-6666-555555555555", response.RequestDeviceId);
        Assert.Equal(false, response.RequestApproved);
        Assert.Equal("auth-request", response.Object);
        Assert.Equal(
            JsonSerializer.Serialize(response, Web),
            JsonSerializer.Serialize(response, ApiJsonContext.Default.AuthRequestResponse));
    }

    [Fact]
    public void AuthRequestResponse_allows_vaultwarden_to_omit_official_metadata()
    {
        const string json =
            """{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","publicKey":"public-key","requestDeviceType":"Windows","requestIpAddress":"127.0.0.1","key":null,"masterPasswordHash":null,"creationDate":"2026-08-30T00:00:00Z","responseDate":null,"requestApproved":null,"origin":"https://vault.example"}""";

        var response = JsonSerializer.Deserialize(
            json,
            ApiJsonContext.Default.AuthRequestResponse)!;

        Assert.Null(response.RequestApproved);
        Assert.Null(response.RequestDeviceIdentifier);
        Assert.Null(response.RequestDeviceTypeValue);
        Assert.Null(response.RequestCountryName);
        Assert.Null(response.RequestDeviceId);
        Assert.Null(response.Object);
    }

    // ── AuthRequestListResponse ─────────────────────────────────────────────

    [Fact]
    public void AuthRequestListResponse_serialises_data_array()
    {
        var resp = new AuthRequestListResponse(
        [
            new AuthRequestResponse(
                "id-001",
                "pk1",
                "Windows",
                "127.0.0.1",
                null,
                null,
                "2025-01-01T00:00:00Z",
                null,
                null,
                null)
        ]);
        var json = JsonSerializer.Serialize(resp, Web);
        Assert.Contains("\"data\":[", json);
        Assert.Contains("\"id\":\"id-001\"", json);
    }

    [Fact]
    public void AuthRequestListResponse_context_matches_reflection()
    {
        var resp = new AuthRequestListResponse(
        [
            new AuthRequestResponse(
                "id-001",
                "pk1",
                "Unknown",
                "127.0.0.1",
                null,
                null,
                "2025-01-01T00:00:00Z",
                null,
                null,
                null)
        ]);
        Assert.Equal(
            JsonSerializer.Serialize(resp, Web),
            JsonSerializer.Serialize(resp, ApiJsonContext.Default.AuthRequestListResponse));
    }

    [Fact]
    public async Task CreateAuthRequest_SendsDeviceIdentifierHeader()
    {
        var handler = new RecordingHandler();
        using var api = new ApiClient(
            new HttpClient(handler),
            new Core.Models.ServerOrigin("https://vault.example"));
        var request = new AuthRequestRequest(
            "access-code",
            "11111111-2222-3333-4444-555555555555",
            "me@example.com",
            "public-key",
            0);

        await ((IPublicAuthRequestApiClient)api).CreateAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/api/auth-requests", handler.RequestUri!.AbsolutePath);
        Assert.Equal(
            request.DeviceIdentifier,
            Assert.Single(handler.DeviceIdentifiers));
        Assert.Equal("6", Assert.Single(handler.DeviceTypes));
        Assert.Contains("\"accessCode\":\"access-code\"", handler.Body);
    }

    [Fact]
    public async Task PublicCreate_RejectsAdminType()
    {
        var handler = new RecordingHandler();
        using var api = new ApiClient(
            new HttpClient(handler),
            new Core.Models.ServerOrigin("https://vault.example"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ((IPublicAuthRequestApiClient)api).CreateAsync(
                new AuthRequestRequest(
                    "1234567890123456789012345",
                    "11111111-2222-3333-4444-555555555555",
                    "me@example.com",
                    "public-key",
                    2),
                TestContext.Current.CancellationToken));

        Assert.Null(handler.Method);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111222233334444555555555555")]
    [InlineData("not-a-guid")]
    public async Task PublicCreate_RejectsInvalidDeviceIdentifier(string deviceIdentifier)
    {
        var handler = new RecordingHandler();
        using var api = new ApiClient(
            new HttpClient(handler),
            new Core.Models.ServerOrigin("https://vault.example"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ((IPublicAuthRequestApiClient)api).CreateAsync(
                new AuthRequestRequest(
                    "1234567890123456789012345",
                    deviceIdentifier,
                    "me@example.com",
                    "public-key",
                    0),
                TestContext.Current.CancellationToken));

        Assert.Null(handler.Method);
    }

    [Fact]
    public async Task AuthenticatedLookup_UsesRequestIdPathWithoutAccessCode()
    {
        var handler = new RecordingHandler();
        using var api = new ApiClient(
            new HttpClient(handler),
            new Core.Models.ServerOrigin("https://vault.example"));

        await ((IAuthRequestApiClient)api).GetAsync(
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("/api/auth-requests/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", handler.RequestUri!.AbsolutePath);
        Assert.Equal(string.Empty, handler.RequestUri.Query);
    }

    [Fact]
    public async Task ResponseAndApproval_UseCanonicalRequestPathsAndEscapedQuery()
    {
        var responseHandler = new RecordingHandler();
        using (var api = new ApiClient(
            new HttpClient(responseHandler),
            new Core.Models.ServerOrigin("https://vault.example")))
        {
            await ((IPublicAuthRequestApiClient)api).GetResponseAsync(
                "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE",
                "code +/?",
                TestContext.Current.CancellationToken);
            Assert.Equal(
                "/api/auth-requests/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/response",
                responseHandler.RequestUri!.AbsolutePath);
            Assert.Equal("?code=code%20%2B%2F%3F", responseHandler.RequestUri.Query);
        }

        var approvalHandler = new RecordingHandler();
        using (var api = new ApiClient(
            new HttpClient(approvalHandler),
            new Core.Models.ServerOrigin("https://vault.example")))
        {
            await ((IAuthRequestApiClient)api).ApproveAsync(
                "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE",
                new AuthResponseRequest(
                    "11111111-2222-3333-4444-555555555555",
                    "encrypted-key",
                    null,
                    true),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpMethod.Put, approvalHandler.Method);
            Assert.Equal(
                "/api/auth-requests/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                approvalHandler.RequestUri!.AbsolutePath);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("not-a-guid")]
    public async Task RequestEndpoints_RejectInvalidIds(string id)
    {
        using var api = new ApiClient(
            new HttpClient(new RecordingHandler()),
            new Core.Models.ServerOrigin("https://vault.example"));
        var response = new AuthResponseRequest(
            "11111111-2222-3333-4444-555555555555",
            "encrypted-key",
            null,
            true);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ((IAuthRequestApiClient)api).GetAsync(
                id,
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ((IAuthRequestApiClient)api).ApproveAsync(
                id,
                response,
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ((IPublicAuthRequestApiClient)api).GetResponseAsync(
                id,
                "1234567890123456789012345",
                TestContext.Current.CancellationToken));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }
        public string[] DeviceIdentifiers { get; private set; } = [];
        public string[] DeviceTypes { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(ct);
            DeviceIdentifiers = request.Headers.TryGetValues(
                "Device-Identifier",
                out var values)
                ? values.ToArray()
                : [];
            DeviceTypes = request.Headers.TryGetValues(
                "Device-Type",
                out var deviceTypes)
                ? deviceTypes.ToArray()
                : [];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"request-id","publicKey":"public-key","requestDeviceType":"Windows","requestIpAddress":"127.0.0.1","key":null,"masterPasswordHash":null,"creationDate":"2026-08-30T00:00:00Z","responseDate":null,"requestApproved":null,"origin":null}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
