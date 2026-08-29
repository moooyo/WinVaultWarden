using System.Security.Cryptography;
using System.Net;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

// Anonymous Send access orchestration. The service endpoints are derived from the
// share URL itself and remain bound to the result used by a later file download.
public sealed class SendAccessService : ISendAccessService
{
    private readonly IVaultApiClientProvider _clients;
    private readonly SendCryptoService _crypto;
    private readonly ITransferIdleTimeoutPolicy _timeoutPolicy;
    private readonly TimeProvider _timeProvider;

    public SendAccessService(
        IVaultApiClientProvider clients,
        SendCryptoService crypto,
        ITransferIdleTimeoutPolicy timeoutPolicy,
        TimeProvider? timeProvider = null)
    {
        _clients = clients;
        _crypto = crypto;
        _timeoutPolicy = timeoutPolicy;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SendAccessResult> AccessAsync(string shareUrl, string? password, CancellationToken ct = default)
    {
        if (!_crypto.TryParseShareUrl(shareUrl, out var parsed) || parsed is null)
            throw new FormatException("无法解析 Send 分享 URL。");
        using (parsed)
        {
            var seed = parsed.CopySeed();
            try
            {
                using var cryptoKey = _crypto.DeriveCryptoKey(seed);
                string? passwordProof = string.IsNullOrEmpty(password)
                    ? null
                    : _crypto.ComputePasswordProof(password, seed);

                var api = _clients.GetAnonymous<IPublicSendApiClient>(parsed.Endpoints);
                var tokenRequestStartedAt = _timeProvider.GetUtcNow();
                var tokenResult = await api.RequestSendAccessTokenAsync(
                    parsed.AccessId,
                    passwordProof,
                    ct);
                SendAccessResponseDto dto;
                SendAccessTokenResult.Success? accessToken = null;
                var usesAccessToken = false;
                if (tokenResult is SendAccessTokenResult.Success token)
                {
                    dto = await api.AccessSendWithTokenAsync(token.AccessToken, ct);
                    accessToken = token;
                    usesAccessToken = true;
                }
                else if (tokenResult is SendAccessTokenResult.Unsupported)
                {
                    dto = await api.AccessSendAsync(parsed.AccessId, passwordProof, ct);
                }
                else
                {
                    throw CreateTokenError((SendAccessTokenResult.Error)tokenResult);
                }

                var type = SendTypePolicy.RequireSupported(dto.Type);
                string? textContent = dto.Text is null ? null : _crypto.DecryptField(dto.Text.Text, cryptoKey);
                string? fileName = dto.File is null ? null : _crypto.DecryptField(dto.File.FileName, cryptoKey);

                var result = new SendAccessResult
                {
                    Type = type,
                    Name = _crypto.DecryptField(dto.Name, cryptoKey) ?? string.Empty,
                    TextContent = textContent,
                    FileName = fileName,
                    FileDownloadUrl = null,
                    SendId = dto.File is not null && !usesAccessToken ? dto.Id : null,
                    FileId = dto.File?.Id,
                    AccessId = parsed.AccessId,
                    Endpoints = parsed.Endpoints,
                    Seed = seed,
                    PasswordProof = passwordProof,
                    UsesAccessToken = usesAccessToken,
                };

                if (accessToken is not null && dto.File is not null)
                {
                    var expiresAt = GetAccessTokenExpiry(
                        tokenRequestStartedAt,
                        accessToken.ExpiresIn);
                    try
                    {
                        result.StoreAccessToken(
                            accessToken.AccessToken,
                            expiresAt);
                    }
                    catch
                    {
                        result.Dispose();
                        throw;
                    }
                }

                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(seed);
            }
        }
    }

    public async Task<long> DownloadFileAsync(
        SendDownloadLease download,
        Stream plaintextDestination,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(plaintextDestination);
        var seed = download.CopySeed();
        try
        {
            var api = _clients.GetAnonymous<IPublicSendApiClient>(download.Endpoints);
            string downloadUrl;

            if (download.UsesAccessToken)
            {
                if (string.IsNullOrEmpty(download.FileId))
                    throw new InvalidOperationException("该 Send 没有可下载的文件。");
                var accessToken = await GetFileAccessTokenAsync(download, api, ct);
                var dlResponse = await api.AccessSendFileWithTokenAsync(
                    download.FileId,
                    accessToken,
                    ct);
                downloadUrl = dlResponse.Url;
            }
            // Legacy Vaultwarden requires an access-file request before downloading the payload.
            else if (!string.IsNullOrEmpty(download.SendId) && !string.IsNullOrEmpty(download.FileId))
            {
                var dlResponse = await api.AccessSendFileAsync(
                    download.SendId, download.FileId, download.PasswordProof, ct);
                downloadUrl = dlResponse.Url;
            }
            else if (!string.IsNullOrEmpty(download.FileDownloadUrl))
            {
                // Compatibility path for callers that already hold the resolved URL.
                downloadUrl = download.FileDownloadUrl;
            }
            else
            {
                throw new InvalidOperationException("该 Send 没有可下载的文件。");
            }

            using var cryptoKey = _crypto.DeriveCryptoKey(seed);
            await using var content = await api.OpenSendFileDownloadAsync(downloadUrl, ct);
            return await _crypto.DecryptFileAsync(
                content.Stream,
                plaintextDestination,
                cryptoKey,
                content.ContentLength,
                _timeoutPolicy,
                ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    private static Exception CreateTokenError(SendAccessTokenResult.Error error)
    {
        if (error.SendAccessErrorType is "password_hash_b64_required" or "password_hash_b64_invalid")
            return new UnauthorizedAccessException("Send password is missing or invalid.");
        if (error.SendAccessErrorType is "email_required" or "email_and_otp_required")
            return new SendUnsupportedAuthenticationException(
                SendAuthType.Email,
                (int)SendAuthType.Email);

        var message = string.IsNullOrWhiteSpace(error.ErrorDescription)
            ? error.ErrorCode ?? "Send access-token request failed."
            : error.ErrorDescription;
        return new VaultApiException(
            (HttpStatusCode)error.HttpStatus,
            error.ErrorDescription,
            message);
    }

    private async Task<string> GetFileAccessTokenAsync(
        SendDownloadLease download,
        IPublicSendApiClient api,
        CancellationToken cancellationToken)
    {
        using (var snapshot = download.TryAcquireAccessTokenSnapshot(_timeProvider.GetUtcNow()))
        {
            if (snapshot is not null)
                return snapshot.Reveal();
        }

        using var refreshLock = await download.AcquireAccessTokenRefreshLockAsync(cancellationToken);
        using (var snapshot = download.TryAcquireAccessTokenSnapshot(_timeProvider.GetUtcNow()))
        {
            if (snapshot is not null)
                return snapshot.Reveal();
        }

        var requestStartedAt = _timeProvider.GetUtcNow();
        var tokenResult = await api.RequestSendAccessTokenAsync(
            download.AccessId,
            download.PasswordProof,
            cancellationToken);
        if (tokenResult is not SendAccessTokenResult.Success token)
        {
            throw tokenResult is SendAccessTokenResult.Error error
                ? CreateTokenError(error)
                : new InvalidOperationException(
                    "The server no longer supports the Send access-token protocol.");
        }

        var expiresAt = GetAccessTokenExpiry(
            requestStartedAt,
            token.ExpiresIn);
        download.StoreAccessToken(token.AccessToken, expiresAt);
        return token.AccessToken;
    }

    private static DateTimeOffset GetAccessTokenExpiry(
        DateTimeOffset requestStartedAt,
        int expiresInSeconds)
    {
        if (expiresInSeconds <= 0)
            throw new InvalidOperationException("The Send access token has an invalid lifetime.");

        return requestStartedAt + TimeSpan.FromSeconds(expiresInSeconds);
    }

}
