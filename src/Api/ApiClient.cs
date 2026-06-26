using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Dtos;
using Core.Abstractions;

namespace Api;

public sealed class ApiClient : IApiClient, IReadonlyApiClient, IVaultWriteApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private Uri? _baseAddress;

    public ApiClient(HttpClient http) => _http = http;

    public void SetBaseAddress(string baseUrl) => _baseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

    public async Task<ConfigResponse> GetConfigAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync(Url("api/config"), ct);
        response.EnsureSuccessStatusCode();
        return await ReadJson<ConfigResponse>(response, ct);
    }

    public async Task<PreloginResponse> PreloginAsync(string email, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(Url("identity/accounts/prelogin"), new PreloginRequest(email), JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await ReadJson<PreloginResponse>(response, ct);
    }

    public async Task<ConnectTokenResult> ConnectTokenAsync(ConnectTokenRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsync(Url("identity/connect/token"), new FormUrlEncodedContent(ToForm(request)), ct);
        if (response.IsSuccessStatusCode)
            return new ConnectTokenResult.Success(await ReadJson<TokenResponse>(response, ct));

        var error = await ReadJsonOrNull<ConnectTokenErrorResponse>(response, ct);
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
        return await ReadJson<SyncResponse>(response, ct);
    }

    public async Task<ListResponse<DeviceDto>> GetDevicesAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync(Url("api/devices"), ct);
        response.EnsureSuccessStatusCode();
        return await ReadJson<ListResponse<DeviceDto>>(response, ct);
    }

    public async Task CreateCipherAsync(CipherRequest request, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Post, "api/ciphers", request, ct);

    public async Task UpdateCipherAsync(string cipherId, CipherRequest request, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Put, $"api/ciphers/{cipherId}", request, ct);

    public async Task SoftDeleteCipherAsync(string cipherId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Put, $"api/ciphers/{cipherId}/delete", ct);

    public async Task HardDeleteCipherAsync(string cipherId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Delete, $"api/ciphers/{cipherId}", ct);

    public async Task RestoreCipherAsync(string cipherId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Put, $"api/ciphers/{cipherId}/restore", ct);

    public async Task CreateFolderAsync(FolderRequest request, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Post, "api/folders", request, ct);

    public async Task UpdateFolderAsync(string folderId, FolderRequest request, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Put, $"api/folders/{folderId}", request, ct);

    public async Task DeleteFolderAsync(string folderId, CancellationToken ct = default)
        => await SendWriteAsync(HttpMethod.Delete, $"api/folders/{folderId}", ct);

    private async Task SendWriteAsync<TBody>(HttpMethod method, string path, TBody body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Url(path))
        {
            Content = JsonContent.Create(body, options: JsonOptions),
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

        var error = await ReadJsonOrNull<WriteErrorResponse>(response, ct);
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

    private static async Task<T> ReadJson<T>(HttpResponseMessage response, CancellationToken ct)
        => await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? throw new JsonException($"Response body is not {typeof(T).Name}.");

    private static async Task<T?> ReadJsonOrNull<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
