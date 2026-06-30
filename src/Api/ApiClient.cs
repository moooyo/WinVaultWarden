using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Api.Dtos;
using Core.Abstractions;

namespace Api;

public sealed class ApiClient : IApiClient, IReadonlyApiClient, IVaultWriteApiClient, ISendApiClient, IAttachmentApiClient, IAccountApiClient, ITwoFactorApiClient, IAuthRequestApiClient
{
    private readonly HttpClient _http;
    private Uri? _baseAddress;

    public ApiClient(HttpClient http) => _http = http;

    public void SetBaseAddress(string baseUrl) => _baseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

    public async Task<ConfigResponse> GetConfigAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync(Url("api/config"), ct);
        response.EnsureSuccessStatusCode();
        return await ReadJson(response, ApiJsonContext.Default.ConfigResponse, ct);
    }

    public async Task<PreloginResponse> PreloginAsync(string email, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            Url("identity/accounts/prelogin"), new PreloginRequest(email),
            ApiJsonContext.Default.PreloginRequest, ct);
        response.EnsureSuccessStatusCode();
        return await ReadJson(response, ApiJsonContext.Default.PreloginResponse, ct);
    }

    public async Task<ConnectTokenResult> ConnectTokenAsync(ConnectTokenRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsync(Url("identity/connect/token"), new FormUrlEncodedContent(ToForm(request)), ct);
        if (response.IsSuccessStatusCode)
            return new ConnectTokenResult.Success(await ReadJson(response, ApiJsonContext.Default.TokenResponse, ct));

        var error = await ReadJsonOrNull(response, ApiJsonContext.Default.ConnectTokenErrorResponse, ct);
        if (response.StatusCode == HttpStatusCode.BadRequest
            && string.Equals(error?.ErrorDescription, "Two factor required.", StringComparison.Ordinal))
        {
            return new ConnectTokenResult.TwoFactorRequired(error?.TwoFactorProviders ?? Array.Empty<int>());
        }

        return new ConnectTokenResult.Error(error?.ErrorDescription ?? error?.Error ?? response.ReasonPhrase ?? "Request failed");
    }

    public async Task<TokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var result = await ConnectTokenAsync(ConnectTokenRequest.Refresh(refreshToken, "refresh", "WinVaultWarden"), ct);
        return result is ConnectTokenResult.Success success
            ? success.Token
            : throw new HttpRequestException("Token refresh failed.");
    }

    public async Task<SyncResponse> GetSyncAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync(Url("api/sync?excludeDomains=true"), ct);
        response.EnsureSuccessStatusCode();
        return await ReadJson(response, ApiJsonContext.Default.SyncResponse, ct);
    }

    public async Task<ListResponse<DeviceDto>> GetDevicesAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync(Url("api/devices"), ct);
        response.EnsureSuccessStatusCode();
        return await ReadJson(response, ApiJsonContext.Default.ListResponseDeviceDto, ct);
    }

    public async Task CreateCipherAsync(CipherRequest request, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Post, "api/ciphers", request, ApiJsonContext.Default.CipherRequest, ct);

    public async Task UpdateCipherAsync(string cipherId, CipherRequest request, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Put, $"api/ciphers/{cipherId}", request, ApiJsonContext.Default.CipherRequest, ct);

    public async Task SoftDeleteCipherAsync(string cipherId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Put, $"api/ciphers/{cipherId}/delete", ct);

    public async Task HardDeleteCipherAsync(string cipherId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Delete, $"api/ciphers/{cipherId}", ct);

    public async Task RestoreCipherAsync(string cipherId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Put, $"api/ciphers/{cipherId}/restore", ct);

    public async Task CreateFolderAsync(FolderRequest request, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Post, "api/folders", request, ApiJsonContext.Default.FolderRequest, ct);

    public async Task UpdateFolderAsync(string folderId, FolderRequest request, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Put, $"api/folders/{folderId}", request, ApiJsonContext.Default.FolderRequest, ct);

    public async Task DeleteFolderAsync(string folderId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Delete, $"api/folders/{folderId}", ct);

    public async Task<FolderDto> GetFolderAsync(string folderId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(Url($"api/folders/{folderId}"), ct);
        response.EnsureSuccessStatusCode();
        return await ReadJson(response, ApiJsonContext.Default.FolderDto, ct);
    }

    // ===== Send =====

    public async Task<SendListResponse> GetSendsAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync(Url("api/sends"), ct);
        response.EnsureSuccessStatusCode();
        return await ReadJson(response, ApiJsonContext.Default.SendListResponse, ct);
    }

    public async Task<SendResponseDto> GetSendAsync(string sendId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(Url($"api/sends/{sendId}"), ct);
        response.EnsureSuccessStatusCode();
        return await ReadJson(response, ApiJsonContext.Default.SendResponseDto, ct);
    }

    public Task<SendResponseDto> CreateTextSendAsync(SendRequest request, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Post, "api/sends", request,
            ApiJsonContext.Default.SendRequest, ApiJsonContext.Default.SendResponseDto, ct);

    public Task<SendFileUploadV2Response> CreateFileSendV2Async(SendRequest request, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Post, "api/sends/file/v2", request,
            ApiJsonContext.Default.SendRequest, ApiJsonContext.Default.SendFileUploadV2Response, ct);

    public async Task UploadSendFileAsync(
        string uploadUrl, string encryptedFileName, byte[] encryptedBuffer, CancellationToken ct = default)
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(encryptedBuffer);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // 字段名固定 "data";文件名必须等于加密后的 fileName(服务端会逐字节比对)。
        content.Add(part, "data", encryptedFileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, Url(NormalizeServerPath(uploadUrl)))
        {
            Content = content,
        };
        await SendWriteCore(request, ct);
    }

    public Task<SendResponseDto> UpdateSendAsync(string sendId, SendRequest request, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Put, $"api/sends/{sendId}", request,
            ApiJsonContext.Default.SendRequest, ApiJsonContext.Default.SendResponseDto, ct);

    public async Task DeleteSendAsync(string sendId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Delete, $"api/sends/{sendId}", ct);

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

    public Task<SendFileDownloadResponse> AccessSendFileAsync(
        string sendId, string fileId, string? passwordProof, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Post, $"api/sends/{sendId}/access/file/{fileId}", new SendAccessRequest(passwordProof),
            ApiJsonContext.Default.SendAccessRequest, ApiJsonContext.Default.SendFileDownloadResponse, ct);

    public async Task<byte[]> DownloadSendFileBytesAsync(string downloadUrl, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(Url(NormalizeServerPath(downloadUrl)), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    // ===== Attachments =====

    public async Task<CipherDto> GetCipherAsync(string cipherId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(Url($"api/ciphers/{cipherId}"), ct);
        response.EnsureSuccessStatusCode();
        return await ReadJson(response, ApiJsonContext.Default.CipherDto, ct);
    }

    public Task<AttachmentUploadV2Response> CreateAttachmentV2Async(
        string cipherId, AttachmentUploadRequest request, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Post, $"api/ciphers/{cipherId}/attachment/v2", request,
            ApiJsonContext.Default.AttachmentUploadRequest, ApiJsonContext.Default.AttachmentUploadV2Response, ct);

    public async Task UploadAttachmentDataAsync(
        string uploadUrl, string encryptedFileName, byte[] encryptedBuffer, CancellationToken ct = default)
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(encryptedBuffer);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // 字段名固定 "data";文件名必须等于加密后的 fileName(服务端逐字节比对)。
        content.Add(part, "data", encryptedFileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, Url(NormalizeServerPath(uploadUrl)))
        {
            Content = content,
        };
        await SendWriteCore(request, ct);
    }

    public async Task DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Delete, $"api/ciphers/{cipherId}/attachment/{attachmentId}", ct);

    public async Task<byte[]> DownloadAttachmentBytesAsync(string downloadUrl, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(Url(NormalizeServerPath(downloadUrl)), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    // ===== Account =====

    public Task UpdateProfileAsync(ProfileUpdateRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, "api/accounts/profile", request, ApiJsonContext.Default.ProfileUpdateRequest, ct);

    public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, "api/accounts/password", request, ApiJsonContext.Default.ChangePasswordRequest, ct);

    public Task ChangeKdfAsync(ChangeKdfRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, "api/accounts/kdf", request, ApiJsonContext.Default.ChangeKdfRequest, ct);

    public Task RegisterAsync(RegisterRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, "identity/accounts/register", request, ApiJsonContext.Default.RegisterRequest, ct);

    // ===== Two-Factor =====

    public async Task<TwoFactorProvidersResponse> GetProvidersAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync(Url("api/two-factor"), ct);
        response.EnsureSuccessStatusCode();
        return await ReadJson(response, ApiJsonContext.Default.TwoFactorProvidersResponse, ct);
    }

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

    public Task<EmailStatusResponse> EnableEmailAsync(EmailVerifyRequest request, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Put, "api/two-factor/email", request,
            ApiJsonContext.Default.EmailVerifyRequest, ApiJsonContext.Default.EmailStatusResponse, ct);

    public Task<RecoverResponse> GetRecoverAsync(PasswordVerifyRequest request, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Post, "api/two-factor/get-recover", request,
            ApiJsonContext.Default.PasswordVerifyRequest, ApiJsonContext.Default.RecoverResponse, ct);

    public Task DisableAsync(DisableTwoFactorRequest request, CancellationToken ct = default)
        => SendWriteAsync(HttpMethod.Post, "api/two-factor/disable", request, ApiJsonContext.Default.DisableTwoFactorRequest, ct);

    // ===== Auth Requests =====

    public async Task<AuthRequestListResponse> GetPendingAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync(Url("api/auth-requests/pending"), ct);
        response.EnsureSuccessStatusCode();
        return await ReadJson(response, ApiJsonContext.Default.AuthRequestListResponse, ct);
    }

    public Task<AuthRequestResponse> ApproveAsync(string id, AuthResponseRequest request, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Put, $"api/auth-requests/{id}", request,
            ApiJsonContext.Default.AuthResponseRequest, ApiJsonContext.Default.AuthRequestResponse, ct);

    public Task<AuthRequestResponse> CreateAsync(AuthRequestRequest request, CancellationToken ct = default)
        => SendWriteReadAsync(
            HttpMethod.Post, "api/auth-requests", request,
            ApiJsonContext.Default.AuthRequestRequest, ApiJsonContext.Default.AuthRequestResponse, ct);

    public async Task<AuthRequestResponse> GetResponseAsync(string id, string code, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(Url($"api/auth-requests/{id}/response?code={Uri.EscapeDataString(code)}"), ct);
        response.EnsureSuccessStatusCode();
        return await ReadJson(response, ApiJsonContext.Default.AuthRequestResponse, ct);
    }

    // 服务端给出的 url 可能是绝对地址,也可能是以 "/" 开头的相对路径。
    // 绝对地址原样返回;相对路径去掉前导 "/" 交给 Url() 拼到 baseAddress。
    // Vaultwarden post_send_file_v2 / post_attachment_v2 返回的上传 URL 不含 /api 前缀,需自动补齐。
    private static string NormalizeServerPath(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out _))
            return url;   // 绝对地址直接用
        var path = url.TrimStart('/');
        // Vaultwarden v2 文件上传 URL: "sends/{id}/file/{fid}" — 补 api/ 前缀。
        if (path.StartsWith("sends/", StringComparison.OrdinalIgnoreCase))
            return "api/" + path;
        // Vaultwarden v2 附件上传 URL: "ciphers/{id}/attachment/{attId}" — 补 api/ 前缀。
        if (path.StartsWith("ciphers/", StringComparison.OrdinalIgnoreCase))
            return "api/" + path;
        return path;
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

    private async Task<TResult> SendWriteCoreRead<TResult>(
        HttpRequestMessage request, JsonTypeInfo<TResult> resultTypeInfo, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return await ReadJson(response, resultTypeInfo, ct);

        var error = await ReadJsonOrNull(response, ApiJsonContext.Default.WriteErrorResponse, ct);
        var message = string.IsNullOrWhiteSpace(error?.Message)
            ? response.ReasonPhrase ?? "Vault write failed."
            : error.Message;
        throw new VaultWriteException(message);
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

    private async Task SendWriteAsync(HttpMethod method, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Url(path));
        await SendWriteCore(request, ct);
    }

    private async Task SendWriteCore(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return;

        var error = await ReadJsonOrNull(response, ApiJsonContext.Default.WriteErrorResponse, ct);
        var message = string.IsNullOrWhiteSpace(error?.Message)
            ? response.ReasonPhrase ?? "Vault write failed."
            : error.Message;
        throw new VaultWriteException(message);
    }

    private Uri Url(string relativePath) =>
        _baseAddress is null
            ? new Uri(relativePath, UriKind.Relative)
            : new Uri(_baseAddress, relativePath);

    private static IEnumerable<KeyValuePair<string, string>> ToForm(ConnectTokenRequest request)
    {
        yield return new("grant_type", request.GrantType);
        yield return new("scope", request.Scope);
        yield return new("client_id", request.ClientId);
        yield return new("device_identifier", request.DeviceIdentifier);
        yield return new("device_name", request.DeviceName);
        yield return new("device_type", request.DeviceType);

        if (!string.IsNullOrEmpty(request.Username))
            yield return new("username", request.Username);
        if (!string.IsNullOrEmpty(request.PasswordHash))
            yield return new("password", request.PasswordHash);
        if (!string.IsNullOrEmpty(request.RefreshToken))
            yield return new("refresh_token", request.RefreshToken);
        if (request.TwoFactor is { } twoFactor)
        {
            yield return new("two_factor_provider", twoFactor.Provider.ToString());
            yield return new("two_factor_token", twoFactor.Token);
            yield return new("two_factor_remember", twoFactor.Remember ? "1" : "0");
        }
    }

    private static async Task<T> ReadJson<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        => await response.Content.ReadFromJsonAsync(typeInfo, ct)
            ?? throw new JsonException($"Response body is not {typeInfo.Type.Name}.");

    private static async Task<T?> ReadJsonOrNull<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(typeInfo, ct);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
