using System.Net;
using System.Text.Json;
using Api.Dtos;
using Core.Models;
using Xunit;

namespace Api.Tests;

public sealed class TemporaryPasswordApiTests
{
    [Fact]
    public void Request_writes_new_payloads_compatible_legacy_fields_and_contained_key_id()
    {
        var kdf = new KdfParams(1, 3, 64, 4);
        var request = new UpdateTemporaryPasswordRequest(
            new AuthData("Case-Sensitive Salt", kdf, "new-auth-hash"),
            new UnlockData(
                "Case-Sensitive Salt",
                kdf,
                "2.master-key-wrapped-user-key",
                "00112233445566778899aabbccddeeff"),
            "new hint");

        var json = JsonSerializer.Serialize(
            request,
            ApiJsonContext.Default.UpdateTemporaryPasswordRequest);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(
            "new-auth-hash",
            root.GetProperty("authenticationData")
                .GetProperty("masterPasswordAuthenticationHash")
                .GetString());
        Assert.Equal(
            "2.master-key-wrapped-user-key",
            root.GetProperty("unlockData")
                .GetProperty("masterKeyWrappedUserKey")
                .GetString());
        Assert.Equal(
            "00112233445566778899aabbccddeeff",
            root.GetProperty("unlockData").GetProperty("containedKeyId").GetString());
        Assert.Equal("new hint", root.GetProperty("masterPasswordHint").GetString());
        Assert.Equal("new-auth-hash", root.GetProperty("newMasterPasswordHash").GetString());
        Assert.Equal("2.master-key-wrapped-user-key", root.GetProperty("key").GetString());
        Assert.False(root.TryGetProperty("resetMasterPassword", out _));
        Assert.False(root.TryGetProperty("resetTwoFactor", out _));
    }

    [Fact]
    public async Task Client_puts_update_temp_password_to_exact_account_route()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        using var client = new ApiClient(
            http,
            new ServerOrigin("https://vault.example"));
        var kdf = new KdfParams(0, 600_000, null, null);

        await client.UpdateTemporaryPasswordAsync(
            new UpdateTemporaryPasswordRequest(
                new AuthData("user@example.com", kdf, "new-auth-hash"),
                new UnlockData("user@example.com", kdf, "2.wrapped-user-key"),
                null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal("/api/accounts/update-temp-password", handler.Uri!.AbsolutePath);
        Assert.Equal(string.Empty, handler.Uri.Query);
        using var document = JsonDocument.Parse(handler.Body!);
        Assert.Equal(
            "new-auth-hash",
            document.RootElement.GetProperty("authenticationData")
                .GetProperty("masterPasswordAuthenticationHash")
                .GetString());
        Assert.Equal(
            "2.wrapped-user-key",
            document.RootElement.GetProperty("unlockData")
                .GetProperty("masterKeyWrappedUserKey")
                .GetString());
        Assert.False(
            document.RootElement.GetProperty("unlockData")
                .TryGetProperty("containedKeyId", out _));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
