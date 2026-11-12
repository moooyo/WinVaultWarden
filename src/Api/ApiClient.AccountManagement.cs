using Api.Dtos;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

namespace Api;

public sealed partial class ApiClient : IAccountManagementApiClient
{
    public Task RequestEmailChangeAsync(AccountEmailTokenRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAccountManagementHash(request.MasterPasswordHash);
        ValidateAccountManagementEmail(request.NewEmail);
        return SendAccountManagementWriteAsync("api/accounts/email-token", request,
            AccountManagementJsonContext.Default.AccountEmailTokenRequest, ct);
    }

    public Task ChangeEmailAsync(AccountEmailChangeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAccountManagementHash(request.MasterPasswordHash);
        ValidateAccountManagementHash(request.NewMasterPasswordHash);
        ValidateAccountManagementEmail(request.NewEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Key);
        if (request.Key.Length > 1024 * 1024 || request.Token.Length > 256)
            throw new ArgumentException("The email-change payload is too large.", nameof(request));
        return SendAccountManagementWriteAsync("api/accounts/email", request,
            AccountManagementJsonContext.Default.AccountEmailChangeRequest, ct);
    }

    public async Task RequestEmailVerificationAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("api/accounts/verify-email"));
        request.Options.Set(AuthHeaderHandler.DisableAutomaticRetryKey, true);
        await SendWriteCore(request, ct).ConfigureAwait(false);
    }

    public Task VerifyEmailTokenAsync(AccountEmailVerificationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Token);
        if (request.UserId.Length > 256 || request.Token.Length > 16 * 1024)
            throw new ArgumentException("The email-verification payload is too large.", nameof(request));
        return SendAccountManagementWriteAsync("api/accounts/verify-email-token", request,
            AccountManagementJsonContext.Default.AccountEmailVerificationRequest, ct);
    }

    public Task RevokeAllSessionsAsync(AccountSecurityStampRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAccountManagementHash(request.MasterPasswordHash);
        return SendAccountManagementWriteAsync("api/accounts/security-stamp", request,
            AccountManagementJsonContext.Default.AccountSecurityStampRequest, ct);
    }

    private static void ValidateAccountManagementHash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 300)
            throw new ArgumentException("The master-password hash is too large.", nameof(value));
    }

    private async Task SendAccountManagementWriteAsync<T>(
        string path, T body, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(path))
        {
            Content = JsonContent.Create(body, typeInfo),
        };
        request.Options.Set(AuthHeaderHandler.DisableAutomaticRetryKey, true);
        using var response = await SendControlAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
            return;
        if (response.IsSuccessStatusCode)
            throw new HttpRequestException("The account-management outcome is unconfirmed.");
        var error = await ReadJsonOrNull(response, ApiJsonContext.Default.WriteErrorResponse, ct)
            .ConfigureAwait(false);
        throw new VaultWriteException(response.StatusCode,
            string.IsNullOrWhiteSpace(error?.Message) ? "Account management failed." : error.Message,
            error?.ValidationErrors);
    }

    private static void ValidateAccountManagementEmail(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 254 || value.Contains('\r') || value.Contains('\n'))
            throw new ArgumentException("The email address is invalid.", nameof(value));
    }
}
