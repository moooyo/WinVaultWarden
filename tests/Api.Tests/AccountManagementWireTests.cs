using System.Net;
using System.Text;
using System.Text.Json;
using Api.Dtos;
using Core.Models;
using Core.Session;
using Xunit;

namespace Api.Tests;

public sealed class AccountManagementWireTests
{
    [Theory]
    [InlineData("email-token")]
    [InlineData("email")]
    [InlineData("verify-email")]
    [InlineData("verify-email-token")]
    [InlineData("security-stamp")]
    public async Task ManagementWrite_UsesExactRouteBearerAndNeverAutomaticallyReplays(string operation)
    {
        using var transport = new Recorder(HttpStatusCode.NoContent);
        using var client = Create(transport);
        var management = Assert.IsAssignableFrom<IAccountManagementApiClient>(client);

        await SendAsync(management, operation, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, transport.Method);
        Assert.Equal($"/warden/api/accounts/{operation}", transport.Uri!.AbsolutePath);
        Assert.Equal("Bearer account-token", transport.Authorization);
        Assert.True(transport.NoRetry);
        if (operation == "verify-email")
        {
            Assert.Null(transport.Body);
            return;
        }
        using var body = JsonDocument.Parse(transport.Body!);
        var root = body.RootElement;
        Assert.False(root.TryGetProperty("password", out _));
        if (operation is "email-token" or "email" or "security-stamp")
            Assert.Equal("old-hash", root.GetProperty("masterPasswordHash").GetString());
        if (operation is "email-token" or "email")
            Assert.Equal("next@example.com", root.GetProperty("newEmail").GetString());
        if (operation == "email")
        {
            Assert.Equal("2.wrapped", root.GetProperty("key").GetString());
            Assert.Equal("new-hash", root.GetProperty("newMasterPasswordHash").GetString());
            Assert.Equal("123456", root.GetProperty("token").GetString());
        }
        if (operation == "verify-email-token")
        {
            Assert.Equal("account", root.GetProperty("userId").GetString());
            Assert.Equal("verification-jwt", root.GetProperty("token").GetString());
        }
    }

    [Fact]
    public async Task SecurityStamp_UnauthorizedIsNotRefreshedOrReplayed()
    {
        using var transport = new Recorder(HttpStatusCode.Unauthorized);
        var refreshCount = 0;
        using var client = Create(transport, () => refreshCount++);
        var management = Assert.IsAssignableFrom<IAccountManagementApiClient>(client);
        await Assert.ThrowsAsync<VaultWriteException>(() => management.RevokeAllSessionsAsync(
            new("old-hash"), TestContext.Current.CancellationToken));
        Assert.Equal(0, refreshCount);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task UnexpectedSuccessStatus_IsAnUnconfirmedOutcome()
    {
        using var transport = new Recorder(HttpStatusCode.Accepted);
        using var client = Create(transport);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            ((IAccountManagementApiClient)client).ChangeEmailAsync(
                new("old-hash", "next@example.com", "2.wrapped", "new-hash", "123456"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StructuredRejectionRetainsStatus()
    {
        using var transport = new Recorder(HttpStatusCode.BadRequest);
        using var client = Create(transport);
        var error = await Assert.ThrowsAsync<VaultWriteException>(() =>
            ((IAccountManagementApiClient)client).RequestEmailChangeAsync(
                new("old-hash", "next@example.com"), TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
    }

    private static Task SendAsync(IAccountManagementApiClient client, string operation, CancellationToken ct) =>
        operation switch
        {
            "email-token" => client.RequestEmailChangeAsync(new("old-hash", "next@example.com"), ct),
            "email" => client.ChangeEmailAsync(new("old-hash", "next@example.com", "2.wrapped", "new-hash", "123456"), ct),
            "verify-email" => client.RequestEmailVerificationAsync(ct),
            "verify-email-token" => client.VerifyEmailTokenAsync(new("account", "verification-jwt"), ct),
            "security-stamp" => client.RevokeAllSessionsAsync(new("old-hash"), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static IAuthenticatedApiClient Create(Recorder recorder, Action? refresh = null)
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example/warden");
        return new ApiClientFactory(_ => recorder).CreateAuthenticated(endpoints,
            () => new AccessTokenBinding("account", endpoints, 1, "account-token"),
            (_, _) =>
            {
                refresh?.Invoke();
                return Task.FromResult(true);
            });
    }

    private sealed class Recorder(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }
        public bool NoRetry { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Method = request.Method;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            NoRetry = request.Options.TryGetValue(AuthHeaderHandler.DisableAutomaticRetryKey, out var value) && value;
            return new(status)
            {
                Content = new StringContent("{\"message\":\"Rejected\"}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
