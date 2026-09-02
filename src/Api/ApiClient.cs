using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Session;

namespace Api;

public sealed class ApiClient :
    IIdentityApiClient,
    IRegistrationApiClient,
    IPublicSendApiClient,
    IPublicAuthRequestApiClient,
    IServerConfigApiClient,
    IAuthenticatedApiClient,
    IDisposable
{
    private const int MaxErrorBodyBytes = 64 * 1024;
    private const int MaxRegistrationTokenBytes = 16 * 1024;
    // Vaultwarden accepts JSON requests up to 20 MiB and create responses echo the cipher.
    private const int MaxCipherWriteResponseBytes = 20 * 1024 * 1024;
    private readonly HttpClient _http;
    public ServerEndpoints Endpoints { get; }
    public ServerOrigin Origin => Endpoints.WebVault.Origin;

    public ApiClient(HttpClient http, ServerEndpoints endpoints)
    {
        _http = http;
        Endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
    }

    public ApiClient(HttpClient http, ServerOrigin origin)
        : this(http, ServerEndpoints.ForSelfHosted(origin.Value))
    {
    }

    public Task<ConfigResponse> GetConfigAsync(CancellationToken ct = default) =>
        GetReadAsync("api/config", ApiJsonContext.Default.ConfigResponse, ct);

    public async Task<PreloginResponse> PreloginAsync(string email, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            Url("identity/accounts/prelogin"))
        {
            Content = JsonContent.Create(
                new PreloginRequest(email),
                ApiJsonContext.Default.PreloginRequest),
        };
        using var response = await SendControlAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadJson(response, ApiJsonContext.Default.PreloginResponse, ct);
    }

    public async Task<ConnectTokenResult> ConnectTokenAsync(ConnectTokenRequest request, CancellationToken ct = default)
    {
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            Url("identity/connect/token"))
        {
            Content = new FormUrlEncodedContent(ToForm(request)),
        };
        using var response = await SendControlAsync(httpRequest, ct);
        if (response.IsSuccessStatusCode)
            return new ConnectTokenResult.Success(await ReadJson(response, ApiJsonContext.Default.TokenResponse, ct));

        var error = await ReadJsonOrNull(response, ApiJsonContext.Default.ConnectTokenErrorResponse, ct);
        if (response.StatusCode == HttpStatusCode.BadRequest
            && string.Equals(error?.ErrorDescription, "Two factor required.", StringComparison.Ordinal))
        {
            return new ConnectTokenResult.TwoFactorRequired(
                error?.TwoFactorProviders ?? Array.Empty<int>(),
                error?.SsoEmail2FaSessionToken,
                error?.Email,
                error?.TwoFactorProviders2);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest
            && string.Equals(
                error?.ErrorModel?.Message,
                "new device verification required",
                StringComparison.Ordinal))
        {
            return new ConnectTokenResult.DeviceVerificationRequired();
        }

        // 错误文案回退链:error_description(非空) → message → error → HTTP ReasonPhrase。
        // vaultwarden 默认错误体把 error/error_description 置空串,真实文案只在 message
        // (error.rs:227/239-240),故不能只读 error_description。HttpStatus/ErrorCode 供刷新逻辑分类。
        var message = FirstNonEmpty(error?.ErrorDescription, error?.Message, error?.Error, response.ReasonPhrase)
            ?? "Request failed";
        var errorCode = string.IsNullOrEmpty(error?.Error) ? null : error!.Error;
        return new ConnectTokenResult.Error(message, (int)response.StatusCode, errorCode);
    }

    public Task<SyncResponse> GetSyncAsync(CancellationToken ct = default) =>
        GetReadAsync(
            "api/sync?excludeDomains=true",
            ApiJsonContext.Default.SyncResponse,
            ct);

    public Task<long> GetAccountRevisionDateAsync(CancellationToken ct = default) =>
        GetReadAsync(
            "api/accounts/revision-date",
            ApiJsonContext.Default.Int64,
            ct);

    public Task<ListResponse<DeviceDto>> GetDevicesAsync(CancellationToken ct = default) =>
        GetReadAsync("api/devices", ApiJsonContext.Default.ListResponseDeviceDto, ct);

    public Task<DeviceDto> UpdateTrustedDeviceKeysAsync(
        string deviceIdentifier,
        TrustedDeviceKeysRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentifier);
        ArgumentNullException.ThrowIfNull(request);
        return SendWriteReadAsync(
            HttpMethod.Put,
            $"api/devices/{Uri.EscapeDataString(deviceIdentifier)}/keys",
            request,
            ApiJsonContext.Default.TrustedDeviceKeysRequest,
            ApiJsonContext.Default.DeviceDto,
            ct);
    }

    public Task UntrustDevicesAsync(
        UntrustDevicesRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendWriteAsync(
            HttpMethod.Post,
            "api/devices/untrust",
            request,
            ApiJsonContext.Default.UntrustDevicesRequest,
            ct);
    }

    public Task<PostAccountKeysResponse> PostAccountKeysAsync(
        PostAccountKeysRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendWriteReadAsync(
            HttpMethod.Post,
            "api/accounts/keys",
            request,
            ApiJsonContext.Default.PostAccountKeysRequest,
            ApiJsonContext.Default.PostAccountKeysResponse,
            ct);
    }

    public Task<OrganizationAutoEnrollStatusResponse> GetAutoEnrollStatusAsync(
        string organizationIdentifier,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationIdentifier);
        if (organizationIdentifier.Length > 256
            || organizationIdentifier.Contains('/')
            || organizationIdentifier.Contains('\\')
            || organizationIdentifier is "." or "..")
        {
            throw new ArgumentException(
                "The organization identifier is invalid.",
                nameof(organizationIdentifier));
        }
        return GetReadAsync(
            $"api/organizations/{Uri.EscapeDataString(organizationIdentifier)}/auto-enroll-status",
            ApiJsonContext.Default.OrganizationAutoEnrollStatusResponse,
            ct);
    }

    public Task<OrganizationKeysResponse> GetOrganizationKeysAsync(
        string organizationId,
        CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(organizationId, "D", out var parsedOrganizationId))
            throw new ArgumentException("The organization identifier is invalid.", nameof(organizationId));
        return GetReadAsync(
            $"api/organizations/{parsedOrganizationId:D}/keys",
            ApiJsonContext.Default.OrganizationKeysResponse,
            ct);
    }

    public Task EnrollResetPasswordAsync(
        string organizationId,
        string userId,
        ResetPasswordEnrollmentRequest request,
        CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(organizationId, "D", out var parsedOrganizationId))
            throw new ArgumentException("The organization identifier is invalid.", nameof(organizationId));
        if (!Guid.TryParseExact(userId, "D", out var parsedUserId))
            throw new ArgumentException("The user identifier is invalid.", nameof(userId));
        ArgumentNullException.ThrowIfNull(request);
        return SendWriteAsync(
            HttpMethod.Put,
            $"api/organizations/{parsedOrganizationId:D}/users/" +
            $"{parsedUserId:D}/reset-password-enrollment",
            request,
            ApiJsonContext.Default.ResetPasswordEnrollmentRequest,
            ct);
    }

    public async Task CreateCipherAsync(CipherRequest request, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Post, "api/ciphers", request, ApiJsonContext.Default.CipherRequest, ct);

    public async Task<CipherDto?> CreateCipherWithResponseAsync(
        CipherRequest request,
        CancellationToken ct = default) =>
        await SendBoundedWriteReadAsync(
            HttpMethod.Post,
            "api/ciphers",
            request,
            ApiJsonContext.Default.CipherRequest,
            ApiJsonContext.Default.CipherDto,
            MaxCipherWriteResponseBytes,
            ct);

    public async Task CreateOrganizationCipherAsync(ShareCipherRequest request, CancellationToken ct = default)
        => await SendWriteAsync(
            HttpMethod.Post,
            "api/ciphers/create",
            request,
            ApiJsonContext.Default.ShareCipherRequest,
            ct);

    public async Task<CipherDto?> CreateOrganizationCipherWithResponseAsync(
        ShareCipherRequest request,
        CancellationToken ct = default) =>
        await SendBoundedWriteReadAsync(
            HttpMethod.Post,
            "api/ciphers/create",
            request,
            ApiJsonContext.Default.ShareCipherRequest,
            ApiJsonContext.Default.CipherDto,
            MaxCipherWriteResponseBytes,
            ct);

    public async Task UpdateCipherAsync(string cipherId, CipherRequest request, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Put, $"api/ciphers/{cipherId}", request, ApiJsonContext.Default.CipherRequest, ct);

    public Task ShareExistingCipherAsync(
        string cipherId,
        ShareCipherRequest request,
        CancellationToken ct = default) =>
        SendWriteAsync(
            HttpMethod.Put,
            $"api/ciphers/{cipherId}/share",
            request,
            ApiJsonContext.Default.ShareCipherRequest,
            ct);

    public Task BulkShareCiphersAsync(
        BulkShareCiphersRequest request,
        CancellationToken ct = default) =>
        SendWriteAsync(
            HttpMethod.Put,
            "api/ciphers/share",
            request,
            ApiJsonContext.Default.BulkShareCiphersRequest,
            ct);

    public Task SetCipherCollectionsAsync(
        string cipherId,
        CipherCollectionsRequest request,
        CancellationToken ct = default) =>
        SendWriteAsync(
            HttpMethod.Put,
            $"api/ciphers/{cipherId}/collections_v2",
            request,
            ApiJsonContext.Default.CipherCollectionsRequest,
            ct);

    public Task BulkUpdateCipherCollectionsAsync(
        BulkCipherCollectionsRequest request,
        CancellationToken ct = default) =>
        SendWriteAsync(
            HttpMethod.Post,
            "api/ciphers/bulk-collections",
            request,
            ApiJsonContext.Default.BulkCipherCollectionsRequest,
            ct);

    public async Task UpdateCipherPartialAsync(
        string cipherId,
        PartialCipherRequest request,
        CancellationToken ct = default) =>
        await SendWriteAsync(
            HttpMethod.Put,
            $"api/ciphers/{cipherId}/partial",
            request,
            ApiJsonContext.Default.PartialCipherRequest,
            ct);

    public async Task SoftDeleteCipherAsync(string cipherId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Put, $"api/ciphers/{cipherId}/delete", ct);

    public async Task HardDeleteCipherAsync(string cipherId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Delete, $"api/ciphers/{cipherId}", ct);

    public async Task RestoreCipherAsync(string cipherId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Put, $"api/ciphers/{cipherId}/restore", ct);

    public async Task ArchiveCipherAsync(string cipherId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Put, $"api/ciphers/{cipherId}/archive", ct);

    public async Task UnarchiveCipherAsync(string cipherId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Put, $"api/ciphers/{cipherId}/unarchive", ct);

    public Task BulkSoftDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Put, "api/ciphers/delete",
            new CipherIdsRequest { Ids = ids.ToArray() }, ApiJsonContext.Default.CipherIdsRequest, ct);

    public Task BulkHardDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, "api/ciphers/delete",
            new CipherIdsRequest { Ids = ids.ToArray() }, ApiJsonContext.Default.CipherIdsRequest, ct);

    public Task BulkRestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Put, "api/ciphers/restore",
            new CipherIdsRequest { Ids = ids.ToArray() }, ApiJsonContext.Default.CipherIdsRequest, ct);

    public Task BulkArchiveCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Put, "api/ciphers/archive",
            new CipherIdsRequest { Ids = ids.ToArray() }, ApiJsonContext.Default.CipherIdsRequest, ct);

    public Task BulkUnarchiveCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Put, "api/ciphers/unarchive",
            new CipherIdsRequest { Ids = ids.ToArray() }, ApiJsonContext.Default.CipherIdsRequest, ct);

    public Task BulkMoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Put, "api/ciphers/move",
            new MoveCiphersRequest { Ids = ids.ToArray(), FolderId = folderId }, ApiJsonContext.Default.MoveCiphersRequest, ct);

    public async Task CreateFolderAsync(FolderRequest request, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Post, "api/folders", request, ApiJsonContext.Default.FolderRequest, ct);

    public async Task UpdateFolderAsync(string folderId, FolderRequest request, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Put, $"api/folders/{folderId}", request, ApiJsonContext.Default.FolderRequest, ct);

    public async Task DeleteFolderAsync(string folderId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Delete, $"api/folders/{folderId}", ct);

    public Task ImportCiphersAsync(ImportRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, "api/ciphers/import", request, ApiJsonContext.Default.ImportRequest, ct);

    public Task<FolderDto> GetFolderAsync(string folderId, CancellationToken ct = default) =>
        GetReadAsync($"api/folders/{folderId}", ApiJsonContext.Default.FolderDto, ct);

    // ===== Send =====

    public Task<SendListResponse> GetSendsAsync(CancellationToken ct = default) =>
        GetReadAsync("api/sends", ApiJsonContext.Default.SendListResponse, ct);

    public Task<SendResponseDto> GetSendAsync(string sendId, CancellationToken ct = default) =>
        GetReadAsync($"api/sends/{sendId}", ApiJsonContext.Default.SendResponseDto, ct);

    public Task<SendResponseDto> CreateTextSendAsync(SendRequest request, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Post, "api/sends", request,
            ApiJsonContext.Default.SendRequest, ApiJsonContext.Default.SendResponseDto, ct);

    public Task<AuthenticatedWriteReceipt<SendFileUploadV2Response>> CreateFileSendV2Async(
        SendRequest request,
        CancellationToken ct = default)
        => SendAuthenticatedWriteReadAsync(
            HttpMethod.Post, "api/sends/file/v2", request,
            ApiJsonContext.Default.SendRequest, ApiJsonContext.Default.SendFileUploadV2Response, ct);

    public async Task UploadSendFileAsync(
        FileUploadType fileUploadType,
        string uploadUrl,
        string sendId,
        string fileId,
        string encryptedFileName,
        StreamUploadContent encryptedContent,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sendId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        await UploadFileDataAsync(
            fileUploadType,
            uploadUrl,
            encryptedFileName,
            encryptedContent,
            NormalizeServerPath,
            $"api/sends/{Uri.EscapeDataString(sendId)}/file/{Uri.EscapeDataString(fileId)}",
            ct);
    }

    public Task<SendResponseDto> UpdateSendAsync(string sendId, SendRequest request, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Put, $"api/sends/{sendId}", request,
            ApiJsonContext.Default.SendRequest, ApiJsonContext.Default.SendResponseDto, ct);

    public async Task DeleteSendAsync(string sendId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Delete, $"api/sends/{sendId}", ct);

    public async Task DeleteSendForReplacementAsync(string sendId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sendId);
        ct.ThrowIfCancellationRequested();

        using var request = new HttpRequestMessage(HttpMethod.Delete, Url($"api/sends/{sendId}"));
        HttpResponseMessage response;
        try
        {
            response = await SendControlAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            throw new SendDeleteOutcomeUncertainException(sendId, ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                return;

            WriteErrorResponse? error = null;
            try
            {
                error = await ReadJsonOrNull(response, ApiJsonContext.Default.WriteErrorResponse, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // The response status is enough to classify the DELETE. Error text is best-effort only.
            }

            var message = string.IsNullOrWhiteSpace(error?.Message)
                ? response.ReasonPhrase
                : error.Message;
            if ((int)response.StatusCode >= 500)
            {
                var serverError = new VaultApiException(
                    response.StatusCode,
                    message,
                    message ?? "Server error.");
                throw new SendDeleteOutcomeUncertainException(
                    sendId,
                    serverError,
                    response.StatusCode);
            }

            throw new SendDeleteDefinitelyNotAppliedException(sendId, response.StatusCode, message);
        }
    }

    public async Task<SendResponseDto> RemoveSendPasswordAsync(string sendId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Url($"api/sends/{sendId}/remove-password"));
        return await SendWriteCoreRead(request, ApiJsonContext.Default.SendResponseDto, ct);
    }

    public Task<SendAccessResponseDto> AccessSendAsync(
        string accessId, string? passwordProof, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Post, $"api/sends/access/{accessId}", new SendAccessRequest(passwordProof),
            ApiJsonContext.Default.SendAccessRequest, ApiJsonContext.Default.SendAccessResponseDto, ct);

    public async Task<SendAccessTokenResult> RequestSendAccessTokenAsync(
        string accessId,
        string? passwordProof,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessId);
        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "send_access"),
            new("client_id", "send"),
            new("scope", "api.send.access"),
            new("send_id", accessId),
        };
        if (!string.IsNullOrWhiteSpace(passwordProof))
            fields.Add(new("password_hash_b64", passwordProof));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            Url("identity/connect/token"))
        {
            Content = new FormUrlEncodedContent(fields),
        };
        using var response = await SendControlAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            var token = await ReadJson(
                response,
                ApiJsonContext.Default.SendAccessTokenResponse,
                ct);
            if (string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresIn <= 0)
                throw new InvalidDataException("Send access token response is invalid.");
            return new SendAccessTokenResult.Success(token.AccessToken, token.ExpiresIn);
        }

        var error = await ReadJsonOrNull(
            response,
            ApiJsonContext.Default.SendAccessTokenErrorResponse,
            ct);
        var unsupported = response.StatusCode == HttpStatusCode.BadRequest
            && string.IsNullOrWhiteSpace(error?.SendAccessErrorType)
            && (string.Equals(
                    error?.Error,
                    "unsupported_grant_type",
                    StringComparison.Ordinal)
                || (string.IsNullOrWhiteSpace(error?.Error)
                    && string.Equals(
                        error?.Message,
                        "Invalid type",
                        StringComparison.Ordinal)));
        if (unsupported)
            return new SendAccessTokenResult.Unsupported();

        return new SendAccessTokenResult.Error(
            (int)response.StatusCode,
            error?.Error,
            FirstNonEmpty(error?.ErrorDescription, error?.Message, response.ReasonPhrase),
            error?.SendAccessErrorType);
    }

    public Task<SendAccessResponseDto> AccessSendWithTokenAsync(
        string accessToken,
        CancellationToken ct = default) =>
        SendBearerWriteReadAsync(
            "api/sends/access/",
            accessToken,
            ApiJsonContext.Default.SendAccessResponseDto,
            ct);

    public Task<SendFileDownloadResponse> AccessSendFileWithTokenAsync(
        string fileId,
        string accessToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        return SendBearerWriteReadAsync(
            $"api/sends/access/file/{fileId}",
            accessToken,
            ApiJsonContext.Default.SendFileDownloadResponse,
            ct);
    }

    public Task<SendFileDownloadResponse> AccessSendFileAsync(
        string sendId, string fileId, string? passwordProof, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Post, $"api/sends/{sendId}/access/file/{fileId}", new SendAccessRequest(passwordProof),
            ApiJsonContext.Default.SendAccessRequest, ApiJsonContext.Default.SendFileDownloadResponse, ct);

    public Task<DownloadContent> OpenSendFileDownloadAsync(
        string downloadUrl,
        CancellationToken ct = default)
        => OpenDownloadAsync(downloadUrl, ct);

    // ===== Attachments =====

    public Task<CipherDto> GetCipherAsync(string cipherId, CancellationToken ct = default) =>
        GetReadAsync($"api/ciphers/{cipherId}", ApiJsonContext.Default.CipherDto, ct);

    public Task<AuthenticatedWriteReceipt<AttachmentUploadV2Response>> CreateAttachmentV2Async(
        string cipherId, AttachmentUploadRequest request, CancellationToken ct = default)
        => SendAuthenticatedWriteReadAsync(
            HttpMethod.Post, $"api/ciphers/{cipherId}/attachment/v2", request,
            ApiJsonContext.Default.AttachmentUploadRequest, ApiJsonContext.Default.AttachmentUploadV2Response, ct);

    public async Task UploadAttachmentDataAsync(
        FileUploadType fileUploadType,
        string uploadUrl,
        string cipherId,
        string attachmentId,
        string encryptedFileName,
        StreamUploadContent encryptedContent,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cipherId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);
        await UploadFileDataAsync(
            fileUploadType,
            uploadUrl,
            encryptedFileName,
            encryptedContent,
            NormalizeAttachmentUploadPath,
            $"api/ciphers/{Uri.EscapeDataString(cipherId)}/attachment/{Uri.EscapeDataString(attachmentId)}/renew",
            ct);
    }

    public async Task DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Delete, $"api/ciphers/{cipherId}/attachment/{attachmentId}", ct);

    public Task<DownloadContent> OpenAttachmentDownloadAsync(
        string downloadUrl,
        CancellationToken ct = default)
        => OpenDownloadAsync(downloadUrl, ct);

    // ===== Account =====

    public Task UpdateProfileAsync(ProfileUpdateRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, "api/accounts/profile", request, ApiJsonContext.Default.ProfileUpdateRequest, ct);

    public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, "api/accounts/password", request, ApiJsonContext.Default.ChangePasswordRequest, ct);

    public Task UpdateTemporaryPasswordAsync(
        UpdateTemporaryPasswordRequest request,
        CancellationToken ct = default)
        => SendWriteAsync(
            HttpMethod.Put,
            "api/accounts/update-temp-password",
            request,
            ApiJsonContext.Default.UpdateTemporaryPasswordRequest,
            ct);

    public Task ChangeKdfAsync(ChangeKdfRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, "api/accounts/kdf", request, ApiJsonContext.Default.ChangeKdfRequest, ct);

    public Task RequestAccountDeletionOtpAsync(CancellationToken ct = default) =>
        SendWriteAsync(HttpMethod.Post, "api/accounts/request-otp", ct);

    public async Task DeleteAccountAsync(
        DeleteAccountRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Delete,
            Url("api/accounts"))
        {
            Content = JsonContent.Create(
                request,
                ApiJsonContext.Default.DeleteAccountRequest),
        };
        httpRequest.Options.Set(AuthHeaderHandler.DisableAutomaticRetryKey, true);
        using var response = await SendControlAsync(httpRequest, ct);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
            return;
        if (response.IsSuccessStatusCode)
            throw new AccountDeletionOutcomeUncertainException(response.StatusCode);

        var error = await ReadJsonOrNull(
            response,
            ApiJsonContext.Default.WriteErrorResponse,
            ct);
        var message = string.IsNullOrWhiteSpace(error?.Message)
            ? response.ReasonPhrase ?? "Account deletion failed."
            : error.Message;
        throw new VaultWriteException(
            response.StatusCode,
            message,
            error?.ValidationErrors);
    }

    public async Task<RegistrationVerificationResult> RequestRegistrationVerificationTokenAsync(
        RegisterVerificationRequest request,
        CancellationToken ct = default)
    {
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            Url("identity/accounts/register/send-verification-email"))
        {
            Content = JsonContent.Create(
                request,
                ApiJsonContext.Default.RegisterVerificationRequest),
        };
        using var response = await SendControlAsync(httpRequest, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadJsonOrNull(
                response,
                ApiJsonContext.Default.WriteErrorResponse,
                ct);
            var message = string.IsNullOrWhiteSpace(error?.Message)
                ? response.ReasonPhrase ?? "Registration verification request failed."
                : error.Message;
            throw new VaultWriteException(response.StatusCode, message, error?.ValidationErrors);
        }

        if (response.StatusCode == HttpStatusCode.NoContent)
            return new RegistrationVerificationResult.EmailVerificationSent();

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidDataException(
                "Registration verification response has an unexpected status code.");
        }

        var token = await ReadBoundedTextAsync(response, MaxRegistrationTokenBytes, ct);
        if (token is null)
            throw new InvalidDataException(
                "Registration verification response must contain a token.");

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                var decoded = JsonSerializer.Deserialize(token, ApiJsonContext.Default.String);
                if (string.IsNullOrWhiteSpace(decoded))
                {
                    throw new InvalidDataException(
                        "Registration verification response must contain a token.");
                }
                return new RegistrationVerificationResult.InlineToken(decoded.Trim());
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    "Registration verification response must be a JSON string.",
                    ex);
            }
        }

        if (!string.Equals(mediaType, "text/plain", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Registration verification response has an unsupported content type.");
        }

        return new RegistrationVerificationResult.InlineToken(token);
    }

    public Task<RegistrationWriteResult> FinishRegistrationAsync(
        RegisterFinishRequest request,
        CancellationToken ct = default) =>
        SendRegistrationWriteAsync(
            HttpMethod.Post,
            "identity/accounts/register/finish",
            request,
            ApiJsonContext.Default.RegisterFinishRequest,
            ct);

    public Task<RegistrationWriteResult> RegisterAsync(
        RegisterRequest request,
        CancellationToken ct = default) =>
        SendRegistrationWriteAsync(
            HttpMethod.Post,
            "identity/accounts/register",
            request,
            ApiJsonContext.Default.RegisterRequest,
            ct);

    // ===== Emergency Access =====
    public Task<ListResponse<EmergencyAccessGranteeDetailsDto>> GetTrustedAsync(
        CancellationToken ct = default) =>
        GetReadAsync(
            "api/emergency-access/trusted",
            ApiJsonContext.Default.ListResponseEmergencyAccessGranteeDetailsDto,
            ct);

    public Task<ListResponse<EmergencyAccessGrantorDetailsDto>> GetGrantedAsync(
        CancellationToken ct = default) =>
        GetReadAsync(
            "api/emergency-access/granted",
            ApiJsonContext.Default.ListResponseEmergencyAccessGrantorDetailsDto,
            ct);

    public Task<PublicKeyResponse> GetPublicKeyAsync(
        string userId,
        CancellationToken ct = default) =>
        GetReadAsync(
            $"api/users/{userId}/public-key",
            ApiJsonContext.Default.PublicKeyResponse,
            ct);

    public Task InviteAsync(EmergencyAccessInviteRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, "api/emergency-access/invite", request, ApiJsonContext.Default.EmergencyAccessInviteRequest, ct);

    public Task ReinviteAsync(string id, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, $"api/emergency-access/{id}/reinvite", ct);

    public Task AcceptAsync(string id, EmergencyAccessAcceptRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, $"api/emergency-access/{id}/accept", request, ApiJsonContext.Default.EmergencyAccessAcceptRequest, ct);

    public Task ConfirmAsync(string id, EmergencyAccessConfirmRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, $"api/emergency-access/{id}/confirm", request,
            ApiJsonContext.Default.EmergencyAccessConfirmRequest, ct);

    public Task UpdateAsync(string id, EmergencyAccessUpdateRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Put, $"api/emergency-access/{id}", request,
            ApiJsonContext.Default.EmergencyAccessUpdateRequest, ct);

    public Task DeleteAsync(string id, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Delete, $"api/emergency-access/{id}", ct);

    public Task InitiateAsync(string id, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, $"api/emergency-access/{id}/initiate", ct);

    public Task ApproveAsync(string id, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, $"api/emergency-access/{id}/approve", ct);

    public Task RejectAsync(string id, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, $"api/emergency-access/{id}/reject", ct);

    public async Task<EmergencyAccessViewResponse> ViewAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Url($"api/emergency-access/{id}/view"));
        return await SendWriteCoreRead(req, ApiJsonContext.Default.EmergencyAccessViewResponse, ct);
    }

    public async Task<EmergencyAccessTakeoverResponse> TakeoverAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Url($"api/emergency-access/{id}/takeover"));
        return await SendWriteCoreRead(req, ApiJsonContext.Default.EmergencyAccessTakeoverResponse, ct);
    }

    public Task PasswordAsync(string id, EmergencyAccessPasswordRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, $"api/emergency-access/{id}/password", request, ApiJsonContext.Default.EmergencyAccessPasswordRequest, ct);

    // ===== Two-Factor =====

    public Task<TwoFactorProvidersResponse> GetProvidersAsync(CancellationToken ct = default) =>
        GetReadAsync("api/two-factor", ApiJsonContext.Default.TwoFactorProvidersResponse, ct);

    public Task<AuthenticatorResponse> GetAuthenticatorAsync(PasswordVerifyRequest request, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Post, "api/two-factor/get-authenticator", request,
            ApiJsonContext.Default.PasswordVerifyRequest, ApiJsonContext.Default.AuthenticatorResponse, ct);

    public Task<AuthenticatorResponse> EnableAuthenticatorAsync(EnableAuthenticatorRequest request, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Post, "api/two-factor/authenticator", request,
            ApiJsonContext.Default.EnableAuthenticatorRequest, ApiJsonContext.Default.AuthenticatorResponse, ct);

    public Task DisableAuthenticatorAsync(DisableAuthenticatorRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Delete, "api/two-factor/authenticator", request, ApiJsonContext.Default.DisableAuthenticatorRequest, ct);

    public Task<EmailStatusResponse> GetEmailAsync(PasswordVerifyRequest request, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Post, "api/two-factor/get-email", request,
            ApiJsonContext.Default.PasswordVerifyRequest, ApiJsonContext.Default.EmailStatusResponse, ct);

    public Task SendEmailAsync(SendEmailRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, "api/two-factor/send-email", request, ApiJsonContext.Default.SendEmailRequest, ct);

    // POST api/two-factor/send-email-login — 未认证的登录期发码（vaultwarden email.rs:40 ClientHeaders）。
    // 与已认证的 send-email（设置页）不同：登录尚未拿到 token 时可用。请求体 {email, masterPasswordHash, deviceIdentifier}。
    public Task SendTwoFactorEmailLoginAsync(SendEmailLoginRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, "api/two-factor/send-email-login",
            request,
            ApiJsonContext.Default.SendEmailLoginRequest, ct);

    public Task ResendNewDeviceOtpAsync(
        ResendNewDeviceOtpRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Email);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MasterPasswordHash);
        if (request.Email.Length > 256)
            throw new ArgumentException("Email cannot exceed 256 characters.", nameof(request));
        if (request.MasterPasswordHash.Length > 300)
            throw new ArgumentException("Master password hash cannot exceed 300 characters.", nameof(request));

        return SendWriteAsync(
            HttpMethod.Post,
            "api/accounts/resend-new-device-otp",
            request,
            ApiJsonContext.Default.ResendNewDeviceOtpRequest,
            ct);
    }

    // 注意:PUT /api/two-factor/email 的成功响应把 enabled 序列化为字符串字面量 "true"
    // (vaultwarden/src/api/core/two_factor/email.rs:241),与 get-email 的真 bool 不同。
    // 该响应体无人消费(上层仅关心是否成功),故不解析 body——避免 "true"→bool 反序列化抛异常
    // 导致启用邮箱2FA 永远显示失败(服务端其实已启用)。
    public Task EnableEmailAsync(EmailVerifyRequest request, CancellationToken ct = default)
        => SendWriteAsync(
            HttpMethod.Put, "api/two-factor/email", request,
            ApiJsonContext.Default.EmailVerifyRequest, ct);

    public Task<RecoverResponse> GetRecoverAsync(PasswordVerifyRequest request, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Post, "api/two-factor/get-recover", request,
            ApiJsonContext.Default.PasswordVerifyRequest, ApiJsonContext.Default.RecoverResponse, ct);

    public Task RequestProtectedActionOtpAsync(CancellationToken ct = default) =>
        SendWriteAsync(HttpMethod.Post, "api/accounts/request-otp", ct);

    public Task<WebAuthnTwoFactorResponse> GetWebAuthnAsync(
        WebAuthnVerificationRequest request,
        CancellationToken ct = default) =>
        SendWriteReadAsync(
            HttpMethod.Post,
            "api/two-factor/get-webauthn",
            request,
            ApiJsonContext.Default.WebAuthnVerificationRequest,
            ApiJsonContext.Default.WebAuthnTwoFactorResponse,
            ct);

    public Task<WebAuthnChallengeResponse> GetWebAuthnChallengeAsync(
        WebAuthnVerificationRequest request,
        CancellationToken ct = default) =>
        SendWriteReadAsync(
            HttpMethod.Post,
            "api/two-factor/get-webauthn-challenge",
            request,
            ApiJsonContext.Default.WebAuthnVerificationRequest,
            ApiJsonContext.Default.WebAuthnChallengeResponse,
            ct);

    public Task<WebAuthnTwoFactorResponse> EnableWebAuthnAsync(
        EnableWebAuthnRequest request,
        CancellationToken ct = default) =>
        SendWriteReadAsync(
            HttpMethod.Put,
            "api/two-factor/webauthn",
            request,
            ApiJsonContext.Default.EnableWebAuthnRequest,
            ApiJsonContext.Default.WebAuthnTwoFactorResponse,
            ct);

    public Task<WebAuthnTwoFactorResponse> DeleteWebAuthnCredentialAsync(
        DeleteWebAuthnCredentialRequest request,
        CancellationToken ct = default) =>
        SendWriteReadAsync(
            HttpMethod.Delete,
            "api/two-factor/webauthn",
            request,
            ApiJsonContext.Default.DeleteWebAuthnCredentialRequest,
            ApiJsonContext.Default.WebAuthnTwoFactorResponse,
            ct);

    public Task DeleteAllWebAuthnCredentialsAsync(
        DeleteAllWebAuthnCredentialsRequest request,
        CancellationToken ct = default) =>
        SendWriteAsync(
            HttpMethod.Delete,
            "api/two-factor/webauthn/all",
            request,
            ApiJsonContext.Default.DeleteAllWebAuthnCredentialsRequest,
            ct);

    public Task DisableAsync(DisableTwoFactorRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, "api/two-factor/disable", request, ApiJsonContext.Default.DisableTwoFactorRequest, ct);

    // ===== Auth Requests =====

    public async Task<bool> GetKnownDeviceAsync(
        string email,
        string deviceIdentifier,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var canonicalDeviceIdentifier = NormalizeRequiredGuid(
            deviceIdentifier,
            nameof(deviceIdentifier));
        var encodedEmail = Convert.ToBase64String(Encoding.UTF8.GetBytes(email))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            Url("api/devices/knowndevice"));
        request.Headers.TryAddWithoutValidation(
            "X-Request-Email",
            encodedEmail);
        request.Headers.TryAddWithoutValidation(
            "X-Device-Identifier",
            canonicalDeviceIdentifier);
        using var response = await SendControlAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadJson(response, ApiJsonContext.Default.Boolean, ct);
    }

    public Task<AuthRequestResponse> CreateAdminAsync(
        AuthRequestRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Type != 2)
            throw new ArgumentException("Administrator auth requests must use type 2.", nameof(request));
        var normalizedRequest = request with
        {
            DeviceIdentifier = NormalizeRequiredGuid(
                request.DeviceIdentifier,
                nameof(request.DeviceIdentifier)),
        };
        return SendWriteReadAsync(
            HttpMethod.Post,
            "api/auth-requests/admin-request",
            normalizedRequest,
            ApiJsonContext.Default.AuthRequestRequest,
            ApiJsonContext.Default.AuthRequestResponse,
            ct);
    }

    public Task<AuthRequestResponse> GetAsync(
        string id,
        CancellationToken ct = default)
    {
        var canonicalId = NormalizeRequiredGuid(id, nameof(id));
        return GetReadAsync(
            $"api/auth-requests/{Uri.EscapeDataString(canonicalId)}",
            ApiJsonContext.Default.AuthRequestResponse,
            ct);
    }

    public Task<AuthRequestListResponse> GetPendingAsync(CancellationToken ct = default) =>
        GetReadAsync("api/auth-requests/pending", ApiJsonContext.Default.AuthRequestListResponse, ct);

    public Task<AuthRequestResponse> ApproveAsync(
        string id,
        AuthResponseRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonicalId = NormalizeRequiredGuid(id, nameof(id));
        var normalizedRequest = request with
        {
            DeviceIdentifier = NormalizeRequiredGuid(
                request.DeviceIdentifier,
                nameof(request.DeviceIdentifier)),
        };
        return SendWriteReadAsync(
            HttpMethod.Put,
            $"api/auth-requests/{Uri.EscapeDataString(canonicalId)}",
            normalizedRequest,
            ApiJsonContext.Default.AuthResponseRequest,
            ApiJsonContext.Default.AuthRequestResponse,
            ct);
    }

    public async Task<AuthRequestResponse> CreateAsync(
        AuthRequestRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Type != 0)
            throw new ArgumentException("Login auth requests must use type 0.", nameof(request));
        var normalizedRequest = request with
        {
            DeviceIdentifier = NormalizeRequiredGuid(
                request.DeviceIdentifier,
                nameof(request.DeviceIdentifier)),
        };
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            Url("api/auth-requests"))
        {
            Content = JsonContent.Create(normalizedRequest, ApiJsonContext.Default.AuthRequestRequest),
        };
        message.Headers.TryAddWithoutValidation(
            "Device-Identifier",
            normalizedRequest.DeviceIdentifier);
        message.Headers.TryAddWithoutValidation(
            "Device-Type",
            ClientHeadersHandler.DeviceType);
        return await SendWriteCoreRead(
            message,
            ApiJsonContext.Default.AuthRequestResponse,
            ct);
    }

    public Task<AuthRequestResponse> GetResponseAsync(
        string id,
        string code,
        CancellationToken ct = default)
    {
        var canonicalId = NormalizeRequiredGuid(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return GetReadAsync(
            $"api/auth-requests/{Uri.EscapeDataString(canonicalId)}/response?code={Uri.EscapeDataString(code)}",
            ApiJsonContext.Default.AuthRequestResponse,
            ct);
    }

    // 服务端给出的 url 可能是绝对地址,也可能是以 "/" 开头的相对路径。
    // 绝对地址原样返回;相对路径去掉前导 "/" 交给 Url() 拼到 baseAddress。
    // Vaultwarden post_send_file_v2 / post_attachment_v2 返回的上传 URL 不含 /api 前缀,需自动补齐。
    private static string NormalizeServerPath(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (url.StartsWith("//", StringComparison.Ordinal)
            || url.Contains('\\'))
        {
            throw new ArgumentException(
                "Protocol-relative and backslash paths are not allowed.",
                nameof(url));
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            if (string.IsNullOrWhiteSpace(absolute.Host)
                || !string.IsNullOrEmpty(absolute.UserInfo)
                || !string.IsNullOrEmpty(absolute.Fragment)
                || !url.StartsWith(absolute.Scheme + "://", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Absolute data URLs require an HTTP(S) authority and no user info or fragment.",
                    nameof(url));
            }
            return url;
        }

        if (HasSchemePrefix(url))
        {
            throw new ArgumentException(
                "Only absolute HTTP(S) data URLs or relative API paths are allowed.",
                nameof(url));
        }

        var path = url.TrimStart('/');
        // Vaultwarden v2 文件上传 URL: "sends/{id}/file/{fid}" — 补 api/ 前缀。
        if (path.StartsWith("sends/", StringComparison.OrdinalIgnoreCase))
            return "api/" + path;
        // Vaultwarden v2 附件上传 URL: "ciphers/{id}/attachment/{attId}" — 补 api/ 前缀。
        if (path.StartsWith("ciphers/", StringComparison.OrdinalIgnoreCase))
            return "api/" + path;
        return path;
    }

    private static string NormalizeAttachmentUploadPath(string url)
    {
        var normalized = NormalizeServerPath(url);
        if (Uri.TryCreate(normalized, UriKind.Absolute, out _))
            return normalized;

        var path = normalized.TrimStart('/');
        if (path.StartsWith("api/ciphers/", StringComparison.OrdinalIgnoreCase))
            return path;

        var pathWithoutQuery = path.Split('?', 2)[0];
        var segments = pathWithoutQuery.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 3
            && string.Equals(segments[1], "attachment", StringComparison.OrdinalIgnoreCase))
        {
            return "api/ciphers/" + path;
        }

        throw new ArgumentException(
            "Relative attachment upload URLs must identify a cipher attachment.",
            nameof(url));
    }

    private async Task UploadFileDataAsync(
        FileUploadType fileUploadType,
        string uploadUrl,
        string encryptedFileName,
        StreamUploadContent encryptedContent,
        Func<string, string> normalizeDirectUrl,
        string renewalPath,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedFileName);
        ArgumentNullException.ThrowIfNull(encryptedContent);

        switch (fileUploadType)
        {
            case FileUploadType.Direct:
                await UploadDirectFileAsync(
                    normalizeDirectUrl(uploadUrl),
                    encryptedFileName,
                    encryptedContent,
                    ct);
                return;
            case FileUploadType.Azure:
                await UploadAzureFileWithRenewalAsync(
                    uploadUrl,
                    encryptedContent,
                    renewalPath,
                    ct);
                return;
            default:
                throw new InvalidDataException(
                    $"Unsupported file upload type: {(int)fileUploadType}.");
        }
    }

    private async Task UploadDirectFileAsync(
        string uploadUrl,
        string encryptedFileName,
        StreamUploadContent encryptedContent,
        CancellationToken ct)
    {
        using var multipart = new MultipartFormDataContent();
        var part = new StreamingHttpContent(encryptedContent);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(part, "data", encryptedFileName);

        using var request = CreateDataRequest(HttpMethod.Post, uploadUrl);
        request.Content = multipart;
        ConfigureStreamingUpload(request);
        await SendStreamingUploadCore(request, ct);
    }

    private async Task UploadAzureFileWithRenewalAsync(
        string uploadUrl,
        StreamUploadContent encryptedContent,
        string renewalPath,
        CancellationToken ct)
    {
        var target = ParseAzureUploadTarget(uploadUrl);
        try
        {
            await UploadAzureFileOnceAsync(target, encryptedContent, ct);
            return;
        }
        catch (Exception exception) when (ShouldRenewAzureUpload(exception, ct))
        {
            ct.ThrowIfCancellationRequested();
        }

        var renewal = await GetReadAsync(
            renewalPath,
            ApiJsonContext.Default.FileUploadRenewalResponse,
            ct);
        if (renewal.FileUploadType != FileUploadType.Azure)
        {
            throw new InvalidDataException(
                $"Renewed upload type must remain Azure, but was {(int)renewal.FileUploadType}.");
        }

        var renewedTarget = ParseAzureUploadTarget(renewal.Url);
        await UploadAzureFileOnceAsync(renewedTarget, encryptedContent, ct);
    }

    private async Task UploadAzureFileOnceAsync(
        AzureUploadTarget target,
        StreamUploadContent encryptedContent,
        CancellationToken ct)
    {
        using var request = CreateDataRequest(HttpMethod.Put, target.Uri.AbsoluteUri);
        request.Content = new StreamingHttpContent(encryptedContent);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        request.Headers.Add(
            "x-ms-date",
            DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture));
        request.Headers.Add("x-ms-version", target.ServiceVersion);
        request.Headers.Authorization = null;
        request.Options.Set(AuthHeaderHandler.SuppressBearerAuthorizationKey, true);
        ConfigureStreamingUpload(request);
        await SendStreamingUploadCore(
            request,
            ct,
            expectedStatusCode: HttpStatusCode.Created);
    }

    private static void ConfigureStreamingUpload(HttpRequestMessage request)
    {
        request.Options.Set(AuthHeaderHandler.DisableAutomaticRetryKey, true);
        request.Options.Set(RequestDeadlineHandler.DisableRequestDeadlineKey, true);
        request.Options.Set(RequestDeadlineHandler.UseStreamingUploadIdleTimeoutKey, true);
    }

    private static bool ShouldRenewAzureUpload(Exception exception, CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested
            || ContainsException<InvalidDataException>(exception)
            || ContainsException<CryptographicException>(exception))
        {
            return false;
        }

        var timedOut = RequestTimeoutClassifier.IsTimeout(exception, callerToken);
        return (exception is HttpRequestException || timedOut)
            && (!ContainsException<OperationCanceledException>(exception) || timedOut);
    }

    private static bool ContainsException<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
                return true;
        }

        return false;
    }

    private static AzureUploadTarget ParseAzureUploadTarget(string uploadUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadUrl);
        if (!uploadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || uploadUrl.Contains('\\')
            || RawAuthorityContainsUserInfo(uploadUrl)
            || !Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "Azure uploads require an absolute HTTPS URL without user info or a fragment.",
                nameof(uploadUrl));
        }

        string? serviceVersion = null;
        var versionCount = 0;
        foreach (var component in uri.Query.TrimStart('?').Split('&', StringSplitOptions.None))
        {
            if (component.Length == 0)
                continue;

            var separator = component.IndexOf('=');
            var encodedName = separator < 0 ? component : component[..separator];
            var encodedValue = separator < 0 ? string.Empty : component[(separator + 1)..];
            var name = DecodeQueryComponent(encodedName, nameof(uploadUrl));
            if (!string.Equals(name, "sv", StringComparison.OrdinalIgnoreCase))
                continue;

            versionCount++;
            serviceVersion = DecodeQueryComponent(encodedValue, nameof(uploadUrl));
        }

        if (versionCount != 1 || string.IsNullOrWhiteSpace(serviceVersion))
        {
            throw new ArgumentException(
                "Azure upload URLs must contain exactly one non-empty 'sv' query parameter.",
                nameof(uploadUrl));
        }

        return new AzureUploadTarget(uri, serviceVersion);
    }

    private static bool RawAuthorityContainsUserInfo(string uploadUrl)
    {
        const int authorityStart = 8;
        var authorityEnd = uploadUrl.IndexOfAny(['/', '?', '#'], authorityStart);
        var authority = authorityEnd < 0
            ? uploadUrl[authorityStart..]
            : uploadUrl[authorityStart..authorityEnd];
        return authority.Contains('@');
    }

    private static string DecodeQueryComponent(string value, string parameterName)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException exception)
        {
            throw new ArgumentException("Azure upload URL query encoding is invalid.", parameterName, exception);
        }
    }

    private readonly record struct AzureUploadTarget(Uri Uri, string ServiceVersion);

    private static bool HasSchemePrefix(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0 || !char.IsLetter(value[0]))
            return false;
        for (var index = 1; index < colon; index++)
        {
            var character = value[index];
            if (!char.IsLetterOrDigit(character)
                && character != '+'
                && character != '-'
                && character != '.')
                return false;
        }
        return true;
    }

    private async Task<TResult> SendWriteReadAsync<TBody, TResult>(
        HttpMethod method, string path, TBody body,
        JsonTypeInfo<TBody> bodyTypeInfo, JsonTypeInfo<TResult> resultTypeInfo, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Url(path))
        {
            Content = JsonContent.Create(body, bodyTypeInfo),
        };
        return await SendWriteCoreRead(request, resultTypeInfo, ct);
    }

    private async Task<RegistrationWriteResult> SendRegistrationWriteAsync<TBody>(
        HttpMethod method,
        string path,
        TBody body,
        JsonTypeInfo<TBody> bodyTypeInfo,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Url(path))
        {
            Content = JsonContent.Create(body, bodyTypeInfo),
        };
        using var response = await SendControlAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.OK)
            return new RegistrationWriteResult.Completed();
        if (response.IsSuccessStatusCode)
            return new RegistrationWriteResult.OutcomeUncertain();

        var error = await ReadJsonOrNull(
            response,
            ApiJsonContext.Default.WriteErrorResponse,
            ct);
        var message = string.IsNullOrWhiteSpace(error?.Message)
            ? response.ReasonPhrase ?? "Registration failed."
            : error.Message;
        throw new VaultWriteException(
            response.StatusCode,
            message,
            error?.ValidationErrors);
    }

    private async Task<TResult> GetReadAsync<TResult>(
        string path,
        JsonTypeInfo<TResult> resultTypeInfo,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(path));
        using var response = await SendControlAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadJson(response, resultTypeInfo, ct);
    }

    private async Task<DownloadContent> OpenDownloadAsync(
        string downloadUrl,
        CancellationToken ct)
    {
        using var request = CreateDataRequest(
            HttpMethod.Get,
            NormalizeServerPath(downloadUrl));
        request.Options.Set(RequestDeadlineHandler.CompleteDeadlineOnHeadersKey, true);
        var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        try
        {
            await EnsureSuccessAsync(response, ct);
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return new DownloadContent(response, stream);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<AuthenticatedWriteReceipt<TResult>> SendAuthenticatedWriteReadAsync<TBody, TResult>(
        HttpMethod method, string path, TBody body,
        JsonTypeInfo<TBody> bodyTypeInfo, JsonTypeInfo<TResult> resultTypeInfo, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Url(path))
        {
            Content = JsonContent.Create(body, bodyTypeInfo),
        };
        var capture = AuthHeaderHandler.CaptureAuthorization(request);
        var value = await SendWriteCoreRead(request, resultTypeInfo, ct);
        var authorization = capture.Authorization
            ?? throw new InvalidOperationException(
                "The successful authenticated write did not capture an access-token binding.");
        return new AuthenticatedWriteReceipt<TResult>(value, authorization);
    }

    private async Task<TResult> SendWriteCoreRead<TResult>(
        HttpRequestMessage request, JsonTypeInfo<TResult> resultTypeInfo, CancellationToken ct)
    {
        using var response = await SendControlAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return await ReadJson(response, resultTypeInfo, ct);

        var error = await ReadJsonOrNull(response, ApiJsonContext.Default.WriteErrorResponse, ct);
        var message = string.IsNullOrWhiteSpace(error?.Message)
            ? response.ReasonPhrase ?? "Vault write failed."
            : error.Message;
        throw new VaultWriteException(response.StatusCode, message, error?.ValidationErrors);
    }

    private async Task<TResult> SendBearerWriteReadAsync<TResult>(
        string path,
        string accessToken,
        JsonTypeInfo<TResult> resultTypeInfo,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(path));
        ClientHeadersHandler.SetScopedBearerAuthorization(
            request,
            Endpoints.Api,
            accessToken);
        return await SendWriteCoreRead(request, resultTypeInfo, ct);
    }

    private async Task SendWriteAsync<TBody>(
        HttpMethod method, string path, TBody body, JsonTypeInfo<TBody> typeInfo, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Url(path))
        {
            Content = JsonContent.Create(body, typeInfo),
        };
        await SendWriteCore(request, ct);
    }

    private async Task<TResult> SendBoundedWriteReadAsync<TBody, TResult>(
        HttpMethod method,
        string path,
        TBody body,
        JsonTypeInfo<TBody> bodyTypeInfo,
        JsonTypeInfo<TResult> resultTypeInfo,
        int maxResponseBytes,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Url(path))
        {
            Content = JsonContent.Create(body, bodyTypeInfo),
        };
        using var response = await SendControlAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            try
            {
                return await ReadBoundedJson(
                    response,
                    resultTypeInfo,
                    maxResponseBytes,
                    ct);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new CipherCreateSuccessResponseReadException(response.StatusCode, ex);
            }
        }

        var error = await ReadJsonOrNull(response, ApiJsonContext.Default.WriteErrorResponse, ct);
        var message = string.IsNullOrWhiteSpace(error?.Message)
            ? response.ReasonPhrase ?? "Vault write failed."
            : error.Message;
        throw new VaultWriteException(response.StatusCode, message, error?.ValidationErrors);
    }

    private async Task SendWriteAsync(HttpMethod method, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Url(path));
        await SendWriteCore(request, ct);
    }

    private async Task SendWriteCore(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await SendControlAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return;

        var error = await ReadJsonOrNull(response, ApiJsonContext.Default.WriteErrorResponse, ct);
        var message = string.IsNullOrWhiteSpace(error?.Message)
            ? response.ReasonPhrase ?? "Vault write failed."
            : error.Message;
        throw new VaultWriteException(response.StatusCode, message, error?.ValidationErrors);
    }

    private Task<HttpResponseMessage> SendControlAsync(
        HttpRequestMessage request,
        CancellationToken ct) =>
        _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

    private async Task SendStreamingUploadCore(
        HttpRequestMessage request,
        CancellationToken ct,
        HttpStatusCode? expectedStatusCode = null)
    {
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (expectedStatusCode is HttpStatusCode expected
                ? response.StatusCode == expected
                : response.IsSuccessStatusCode)
            return;

        var error = await ReadJsonOrNull(
            response,
            ApiJsonContext.Default.WriteErrorResponse,
            ct);
        var message = string.IsNullOrWhiteSpace(error?.Message)
            ? response.ReasonPhrase ?? "Vault upload failed."
            : error.Message;
        throw new VaultWriteException(response.StatusCode, message, error?.ValidationErrors);
    }

    private Uri Url(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var relative = path.TrimStart('/');
        if (TryStripRoutePrefix(relative, "api", out var apiPath))
            return Endpoints.Api.Resolve(apiPath);
        if (TryStripRoutePrefix(relative, "identity", out var identityPath))
            return Endpoints.Identity.Resolve(identityPath);
        throw new ArgumentException(
            "API paths must start with an 'api/' or 'identity/' route segment.",
            nameof(path));
    }

    private static bool TryStripRoutePrefix(string path, string prefix, out string remainder)
    {
        if (path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            remainder = path[(prefix.Length + 1)..];
            return true;
        }
        remainder = string.Empty;
        return false;
    }

    // 服务端下发的附件/Send 文件上传·下载 URL 可能指向异源对象存储(S3/Azure Blob 等)。
    // 这类绝对 http(s) URL 按原样使用,不做同源校验;认证 transport 的 AuthHeaderHandler 已保证
    // 跨源请求不携带 Bearer(仅在 request/client/token origin 三者全等时附加),故不泄露令牌。
    // 相对路径仍按本 client 的 Origin 解析(经 NormalizeServerPath 补 api/ 前缀)。
    private HttpRequestMessage CreateDataRequest(HttpMethod method, string relativeOrAbsolute)
    {
        var isAbsolute = Uri.TryCreate(relativeOrAbsolute, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps);
        var relative = relativeOrAbsolute.TrimStart('/');
        var target = absolute;
        if (!isAbsolute)
        {
            target = TryStripRoutePrefix(relative, "api", out var apiPath)
                ? Endpoints.Api.Resolve(apiPath)
                : Endpoints.Api.Resolve(relative);
        }
        var request = new HttpRequestMessage(
            method,
            target);
        return request;
    }

    public void Dispose() => _http.Dispose();

    private static IEnumerable<KeyValuePair<string, string>> ToForm(ConnectTokenRequest request)
    {
        yield return new("grant_type", request.GrantType);
        yield return new("scope", request.Scope);
        yield return new("client_id", request.ClientId);
        yield return new("deviceIdentifier", request.DeviceIdentifier);
        yield return new("deviceName", request.DeviceName);
        yield return new("deviceType", request.DeviceType);

        if (!string.IsNullOrEmpty(request.Username))
            yield return new("username", request.Username);
        if (!string.IsNullOrEmpty(request.PasswordHash))
            yield return new("password", request.PasswordHash);
        if (!string.IsNullOrEmpty(request.AuthRequestId))
            yield return new("authRequest", request.AuthRequestId);
        if (!string.IsNullOrEmpty(request.RefreshToken))
            yield return new("refresh_token", request.RefreshToken);
        if (!string.IsNullOrEmpty(request.Code))
            yield return new("code", request.Code);
        if (!string.IsNullOrEmpty(request.CodeVerifier))
            yield return new("code_verifier", request.CodeVerifier);
        if (!string.IsNullOrEmpty(request.RedirectUri))
            yield return new("redirect_uri", request.RedirectUri);
        if (string.Equals(request.GrantType, "password", StringComparison.Ordinal)
            && !string.IsNullOrEmpty(request.NewDeviceOtp))
        {
            yield return new("newDeviceOtp", request.NewDeviceOtp);
        }
        if (request.TwoFactor is { } twoFactor)
        {
            yield return new("twoFactorProvider", twoFactor.Provider.ToString());
            yield return new("twoFactorToken", twoFactor.Token);
            yield return new("twoFactorRemember", twoFactor.Remember ? "1" : "0");
        }
    }

    // 读/GET 路径统一错误契约:失败时解析服务端错误体(message),抛携带状态码与消息的
    // VaultApiException(其派生自 HttpRequestException,现有异常处理器行为不变)。
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        string? serverMsg = null;
        try
        {
            var body = await ReadJsonOrNull(
                response,
                ApiJsonContext.Default.WriteErrorResponse,
                ct);
            serverMsg = body?.Message;
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException
            && !RequestTimeoutClassifier.IsTimeout(ex, ct))
        {
            // 错误体不是可解析的 JSON(如代理返回的 HTML),回退 ReasonPhrase;
            // 读取诊断体失败绝不能掩盖底层 HTTP 错误。取消需继续冒泡。
        }

        throw new VaultApiException(
            response.StatusCode,
            serverMsg,
            $"请求失败 ({(int)response.StatusCode} {response.ReasonPhrase}): {serverMsg ?? response.ReasonPhrase}");
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        return null;
    }

    private static string NormalizeRequiredGuid(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty)
        {
            throw new ArgumentException(
                "The value must be a non-empty GUID in canonical D format.",
                parameterName);
        }

        return parsed.ToString("D");
    }

    private static async Task<T> ReadJson<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        => await response.Content.ReadFromJsonAsync(typeInfo, ct)
            ?? throw new JsonException($"Response body is not {typeInfo.Type.Name}.");

    private static async Task<T> ReadBoundedJson<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        int maxBytes,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength > maxBytes)
        {
            throw new InvalidDataException("Cipher write response is too large.");
        }

        try
        {
            using var buffer = new DiagnosticBufferStream(maxBytes);
            await response.Content.CopyToAsync(buffer, ct);
            return JsonSerializer.Deserialize(buffer.WrittenSpan, typeInfo)
                ?? throw new JsonException($"Response body is not {typeInfo.Type.Name}.");
        }
        catch (DiagnosticBodyTooLargeException ex)
        {
            throw new InvalidDataException("Cipher write response is too large.", ex);
        }
    }

    private static async Task<T?> ReadJsonOrNull<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxErrorBodyBytes)
            return default;

        try
        {
            using var buffer = new DiagnosticBufferStream(MaxErrorBodyBytes);
            await response.Content.CopyToAsync(buffer, ct);
            return JsonSerializer.Deserialize(buffer.WrittenSpan, typeInfo);
        }
        catch (DiagnosticBodyTooLargeException)
        {
            return default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static async Task<string?> ReadBoundedTextAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength > maxBytes)
            throw new InvalidDataException("Registration verification response is too large.");

        try
        {
            using var buffer = new DiagnosticBufferStream(maxBytes);
            await response.Content.CopyToAsync(buffer, ct);
            var value = Encoding.UTF8.GetString(buffer.WrittenSpan).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (DiagnosticBodyTooLargeException ex)
        {
            throw new InvalidDataException(
                "Registration verification response is too large.",
                ex);
        }
    }

    private sealed class DiagnosticBufferStream : Stream
    {
        private readonly byte[] _buffer;
        private int _length;

        public DiagnosticBufferStream(int maxBytes) => _buffer = new byte[maxBytes];

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > _buffer.Length - _length)
                throw new DiagnosticBodyTooLargeException();

            buffer.CopyTo(_buffer.AsSpan(_length));
            _length += buffer.Length;
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CryptographicOperations.ZeroMemory(_buffer);
                _length = 0;
            }
            base.Dispose(disposing);
        }
    }

    private sealed class DiagnosticBodyTooLargeException : Exception
    {
    }
}
