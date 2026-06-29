using System.Text.Json;
using Api;
using Api.Dtos;
using Xunit;

namespace Api.Tests;

/// <summary>
/// 断言 2FA DTOs 序列化时字段名与 Vaultwarden API 约定完全一致（camelCase）。
/// </summary>
public class TwoFactorWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ── PasswordVerifyRequest ────────────────────────────────────────────────

    [Fact]
    public void PasswordVerifyRequest_serialises_masterPasswordHash()
    {
        var req = new PasswordVerifyRequest("hash-abc");
        var json = JsonSerializer.Serialize(req, Web);
        Assert.Contains("\"masterPasswordHash\":\"hash-abc\"", json);
    }

    // ── AuthenticatorResponse ────────────────────────────────────────────────

    [Fact]
    public void AuthenticatorResponse_serialises_enabled_and_key()
    {
        var resp = new AuthenticatorResponse(true, "JBSWY3DPEHPK3PXP");
        var json = JsonSerializer.Serialize(resp, Web);
        Assert.Contains("\"enabled\":true", json);
        Assert.Contains("\"key\":\"JBSWY3DPEHPK3PXP\"", json);
    }

    // ── EnableAuthenticatorRequest ───────────────────────────────────────────

    [Fact]
    public void EnableAuthenticatorRequest_serialises_key_token_masterPasswordHash()
    {
        var req = new EnableAuthenticatorRequest("SECRETKEY", "123456", "hash-xyz");
        var json = JsonSerializer.Serialize(req, Web);
        Assert.Contains("\"key\":\"SECRETKEY\"", json);
        Assert.Contains("\"token\":\"123456\"", json);
        Assert.Contains("\"masterPasswordHash\":\"hash-xyz\"", json);
    }

    // ── DisableTwoFactorRequest ──────────────────────────────────────────────

    [Fact]
    public void DisableTwoFactorRequest_serialises_masterPasswordHash_and_type()
    {
        var req = new DisableTwoFactorRequest("hash-abc", 0);
        var json = JsonSerializer.Serialize(req, Web);
        Assert.Contains("\"masterPasswordHash\":\"hash-abc\"", json);
        Assert.Contains("\"type\":0", json);
    }

    // ── EmailStatusResponse ──────────────────────────────────────────────────

    [Fact]
    public void EmailStatusResponse_serialises_email_and_enabled()
    {
        var resp = new EmailStatusResponse("user@example.com", false);
        var json = JsonSerializer.Serialize(resp, Web);
        Assert.Contains("\"email\":\"user@example.com\"", json);
        Assert.Contains("\"enabled\":false", json);
    }

    // ── SendEmailRequest ─────────────────────────────────────────────────────

    [Fact]
    public void SendEmailRequest_serialises_email_and_masterPasswordHash()
    {
        var req = new SendEmailRequest("user@example.com", "hash-abc");
        var json = JsonSerializer.Serialize(req, Web);
        Assert.Contains("\"email\":\"user@example.com\"", json);
        Assert.Contains("\"masterPasswordHash\":\"hash-abc\"", json);
    }

    // ── EmailVerifyRequest ───────────────────────────────────────────────────

    [Fact]
    public void EmailVerifyRequest_serialises_email_token_masterPasswordHash()
    {
        var req = new EmailVerifyRequest("user@example.com", "654321", "hash-abc");
        var json = JsonSerializer.Serialize(req, Web);
        Assert.Contains("\"email\":\"user@example.com\"", json);
        Assert.Contains("\"token\":\"654321\"", json);
        Assert.Contains("\"masterPasswordHash\":\"hash-abc\"", json);
    }

    // ── RecoverResponse ──────────────────────────────────────────────────────

    [Fact]
    public void RecoverResponse_serialises_code()
    {
        var resp = new RecoverResponse("ABCD-1234-EFGH-5678");
        var json = JsonSerializer.Serialize(resp, Web);
        Assert.Contains("\"code\":\"ABCD-1234-EFGH-5678\"", json);
    }

    [Fact]
    public void RecoverResponse_deserialises_null_code_without_throwing()
    {
        // 服务端在恢复码尚未生成时返回 {"code":null}，不应抛异常。
        var resp = JsonSerializer.Deserialize<RecoverResponse>("{\"code\":null}", Web);
        Assert.NotNull(resp);
        Assert.Null(resp!.Code);
    }

    // ── TwoFactorProviderItem + TwoFactorProvidersResponse ───────────────────

    [Fact]
    public void TwoFactorProviderItem_serialises_type_and_enabled()
    {
        var item = new TwoFactorProviderItem(0, true);
        var json = JsonSerializer.Serialize(item, Web);
        Assert.Contains("\"type\":0", json);
        Assert.Contains("\"enabled\":true", json);
    }

    [Fact]
    public void TwoFactorProvidersResponse_serialises_data_array()
    {
        var resp = new TwoFactorProvidersResponse(
        [
            new TwoFactorProviderItem(0, true),
            new TwoFactorProviderItem(1, false)
        ]);
        var json = JsonSerializer.Serialize(resp, Web);
        // 列表字段必须是 "data"（Vaultwarden ListResponse 约定）
        Assert.Contains("\"data\":[", json);
        Assert.Contains("\"type\":0", json);
        Assert.Contains("\"enabled\":true", json);
        Assert.Contains("\"type\":1", json);
        Assert.Contains("\"enabled\":false", json);
    }

    // ── AOT / Source-gen 等价断言 ────────────────────────────────────────────

    [Fact]
    public void PasswordVerifyRequest_context_matches_reflection()
    {
        var req = new PasswordVerifyRequest("hash");
        Assert.Equal(
            JsonSerializer.Serialize(req, Web),
            JsonSerializer.Serialize(req, ApiJsonContext.Default.PasswordVerifyRequest));
    }

    [Fact]
    public void TwoFactorProvidersResponse_context_matches_reflection()
    {
        var resp = new TwoFactorProvidersResponse([new TwoFactorProviderItem(0, true)]);
        Assert.Equal(
            JsonSerializer.Serialize(resp, Web),
            JsonSerializer.Serialize(resp, ApiJsonContext.Default.TwoFactorProvidersResponse));
    }
}
