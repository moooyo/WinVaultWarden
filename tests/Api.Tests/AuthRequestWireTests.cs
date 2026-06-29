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
        var req = new AuthRequestRequest("code-abc", "dev-123", "user@example.com", "pub-key-xyz");
        var json = JsonSerializer.Serialize(req, Web);
        Assert.Contains("\"accessCode\":\"code-abc\"", json);
        Assert.Contains("\"deviceIdentifier\":\"dev-123\"", json);
        Assert.Contains("\"email\":\"user@example.com\"", json);
        Assert.Contains("\"publicKey\":\"pub-key-xyz\"", json);
    }

    [Fact]
    public void AuthRequestRequest_context_matches_reflection()
    {
        var req = new AuthRequestRequest("code", "dev-id", "user@test.com", "pub-key");
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
            1,
            "192.168.1.1",
            "enc-key",
            "hash-abc",
            "2025-01-01T00:00:00Z",
            "2025-01-01T00:01:00Z",
            true,
            "https://app.bitwarden.com");
        var json = JsonSerializer.Serialize(resp, Web);
        Assert.Contains("\"requestDeviceType\":1", json);
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
            0,
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

    // ── AuthRequestListResponse ─────────────────────────────────────────────

    [Fact]
    public void AuthRequestListResponse_serialises_data_array()
    {
        var resp = new AuthRequestListResponse(
        [
            new AuthRequestResponse(
                "id-001",
                "pk1",
                1,
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
                0,
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
}
