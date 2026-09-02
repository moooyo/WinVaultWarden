using System.Net;
using System.Text;
using System.Text.Json;
using Api.Dtos;
using Core.Models;
using Core.Session;
using Xunit;

namespace Api.Tests;

public sealed class AccountDeletionWireTests
{
    [Fact]
    public void Factories_WriteOnlyTheSelectedTrimmedField_WithSourceGeneration()
    {
        var passwordRequest = DeleteAccountRequest.FromMasterPasswordHash("  master-hash  ");
        var otpRequest = DeleteAccountRequest.FromOtp("  123456  ");

        Assert.Equal(
            """{"masterPasswordHash":"master-hash"}""",
            JsonSerializer.Serialize(
                passwordRequest,
                ApiJsonContext.Default.DeleteAccountRequest));
        Assert.Equal(
            """{"otp":"123456"}""",
            JsonSerializer.Serialize(
                otpRequest,
                ApiJsonContext.Default.DeleteAccountRequest));

        var deserialized = JsonSerializer.Deserialize(
            """{"otp":"  654321  "}""",
            ApiJsonContext.Default.DeleteAccountRequest)!;
        Assert.Null(deserialized.MasterPasswordHash);
        Assert.Equal("654321", deserialized.Otp);
    }

    [Fact]
    public void Constructor_RejectsMissingAmbiguousBlankAndUnreasonablyLongVerification()
    {
        Assert.Throws<ArgumentException>(() => new DeleteAccountRequest(null, null));
        Assert.Throws<ArgumentException>(() => new DeleteAccountRequest("hash", "123456"));
        Assert.Throws<ArgumentException>(() => DeleteAccountRequest.FromMasterPasswordHash("  "));
        Assert.Throws<ArgumentException>(() => DeleteAccountRequest.FromOtp("  "));
        Assert.Throws<ArgumentException>(() =>
            DeleteAccountRequest.FromMasterPasswordHash(new string('h', 301)));
        Assert.Throws<ArgumentException>(() =>
            DeleteAccountRequest.FromOtp(new string('1', 65)));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task DeleteAccount_UsesAccountRoleBearerExactRouteAndEmptySuccess(
        HttpStatusCode statusCode)
    {
        var transport = new RecordingHandler(_ => new HttpResponseMessage(statusCode));
        var (aggregate, client) = CreateAuthenticatedClient(transport);
        using var owned = Assert.IsAssignableFrom<IDisposable>(aggregate);

        await client.DeleteAccountAsync(
            DeleteAccountRequest.FromMasterPasswordHash("master-hash"),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(transport.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/warden/api/accounts", request.Uri.AbsolutePath);
        Assert.Equal(string.Empty, request.Uri.Query);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal("account-token", request.AuthorizationParameter);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("""{"masterPasswordHash":"master-hash"}""", request.Body);
        Assert.True(request.AutomaticRetryDisabled);
        Assert.Contains(
            typeof(IAccountApiClient).GetMethods(),
            method => method.Name == nameof(IAccountApiClient.DeleteAccountAsync));
        Assert.DoesNotContain(
            typeof(IEmergencyAccessApiClient).GetMethods(),
            method => method.Name == nameof(IAccountApiClient.DeleteAccountAsync));
    }

    [Fact]
    public async Task AccountDeletionOtp_UsesAccountRoleBearerExactRouteAndNoBody()
    {
        var transport = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var (aggregate, client) = CreateAuthenticatedClient(transport);
        using var owned = Assert.IsAssignableFrom<IDisposable>(aggregate);

        await client.RequestAccountDeletionOtpAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(transport.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/warden/api/accounts/request-otp", request.Uri.AbsolutePath);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal("account-token", request.AuthorizationParameter);
        Assert.Null(request.ContentType);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task DeleteAccount_PropagatesStructuredWriteError()
    {
        var transport = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"message":"Invalid password","validationErrors":{"masterPasswordHash":["Invalid password"]}}""",
                Encoding.UTF8,
                "application/json"),
        });
        var (aggregate, client) = CreateAuthenticatedClient(transport);
        using var owned = Assert.IsAssignableFrom<IDisposable>(aggregate);

        var exception = await Assert.ThrowsAsync<VaultWriteException>(() =>
            client.DeleteAccountAsync(
                DeleteAccountRequest.FromMasterPasswordHash("wrong-hash"),
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("Invalid password", exception.Message);
        Assert.Equal(
            ["Invalid password"],
            exception.ValidationErrors!["masterPasswordHash"]);
    }

    [Fact]
    public async Task DeleteAccount_UnauthorizedDoesNotRefreshOrRetry()
    {
        var refreshCalls = 0;
        var transport = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"message":"Access token expired"}""",
                Encoding.UTF8,
                "application/json"),
        });
        var (aggregate, client) = CreateAuthenticatedClient(
            transport,
            (_, _) =>
            {
                refreshCalls++;
                return Task.FromResult(true);
            });
        using var owned = Assert.IsAssignableFrom<IDisposable>(aggregate);

        var exception = await Assert.ThrowsAsync<VaultWriteException>(() =>
            client.DeleteAccountAsync(
                DeleteAccountRequest.FromOtp("123456"),
                TestContext.Current.CancellationToken));

        var request = Assert.Single(transport.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.True(request.AutomaticRetryDisabled);
        Assert.Equal(0, refreshCalls);
        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal("Access token expired", exception.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.PartialContent)]
    [InlineData(HttpStatusCode.MultiStatus)]
    public async Task DeleteAccount_UnexpectedSuccessStatusIsOutcomeUncertain(
        HttpStatusCode statusCode)
    {
        var transport = new RecordingHandler(_ => new HttpResponseMessage(statusCode));
        var (aggregate, client) = CreateAuthenticatedClient(transport);
        using var owned = Assert.IsAssignableFrom<IDisposable>(aggregate);

        var exception = await Assert.ThrowsAsync<AccountDeletionOutcomeUncertainException>(() =>
            client.DeleteAccountAsync(
                DeleteAccountRequest.FromMasterPasswordHash("master-hash"),
                TestContext.Current.CancellationToken));

        Assert.Equal(statusCode, exception.StatusCode);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.True(request.AutomaticRetryDisabled);
    }

    private static (IAuthenticatedApiClient Aggregate, IAccountApiClient Account)
        CreateAuthenticatedClient(
            RecordingHandler transport,
            Func<AccessTokenBinding, CancellationToken, Task<bool>>? refreshAsync = null)
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example/warden");
        var factory = new ApiClientFactory(_ => transport);
        var aggregate = factory.CreateAuthenticated(
            endpoints,
            () => new AccessTokenBinding("account", endpoints, 1, "account-token"),
            refreshAsync ?? ((_, _) => Task.FromResult(false)));
        return (aggregate, Assert.IsAssignableFrom<IAccountApiClient>(aggregate));
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string? ContentType,
        string? Body,
        bool AutomaticRetryDisabled);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
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
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Content?.Headers.ContentType?.MediaType,
                body,
                request.Options.TryGetValue(
                    AuthHeaderHandler.DisableAutomaticRetryKey,
                    out var automaticRetryDisabled)
                    && automaticRetryDisabled));
            return respond(request);
        }
    }
}
