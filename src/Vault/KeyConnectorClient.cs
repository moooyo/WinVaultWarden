using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Core.Models;
using Core.Services;

namespace Vault;

public sealed class KeyConnectorClient : IKeyConnectorClient, IDisposable
{
    private const int MaxResponseBytes = 16 * 1024;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public KeyConnectorClient()
        : this(
            new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                UseCookies = false,
            }),
            ownsClient: true)
    {
    }

    public KeyConnectorClient(HttpClient http)
        : this(http, ownsClient: false)
    {
    }

    private KeyConnectorClient(HttpClient http, bool ownsClient)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ownsClient = ownsClient;
    }

    public async Task<byte[]> GetMasterKeyAsync(
        string keyConnectorUrl,
        string accessToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        if (accessToken.Length > 64 * 1024)
            throw new ArgumentException("The access token is too large.", nameof(accessToken));

        var target = KeyConnectorEndpointPolicy.ResolveUserKeys(keyConnectorUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != target)
            throw new HttpRequestException("Key Connector redirected the user-key request.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                "Key Connector rejected the user-key request.",
                null,
                response.StatusCode);

        var body = await ReadBoundedAsync(response.Content, ct).ConfigureAwait(false);
        byte[]? decodedKey = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            JsonElement pascalKey = default;
            JsonElement camelKey = default;
            var hasPascalKey = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("Key", out pascalKey);
            var hasCamelKey = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("key", out camelKey);
            var keyElement = hasPascalKey ? pascalKey : camelKey;
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || hasPascalKey == hasCamelKey
                || keyElement.ValueKind != JsonValueKind.String
                || keyElement.GetString() is not { Length: > 0 and <= 128 } encodedKey)
            {
                throw new InvalidDataException("Key Connector returned an invalid user key.");
            }

            decodedKey = Convert.FromBase64String(encodedKey);
            if (decodedKey.Length != 32)
            {
                if (decodedKey.Length == 64)
                {
                    throw new InvalidDataException(
                        "This Key Connector key format is not supported by this client.");
                }
                throw new InvalidDataException("Key Connector returned an invalid user key.");
            }
            var result = decodedKey;
            decodedKey = null;
            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Key Connector returned invalid JSON.", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Key Connector returned an invalid user key.", ex);
        }
        finally
        {
            if (decodedKey is not null)
                CryptographicOperations.ZeroMemory(decodedKey);
            CryptographicOperations.ZeroMemory(body);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                    return output.ToArray();
                if (output.Length + read > MaxResponseBytes)
                    throw new InvalidDataException("Key Connector response is too large.");
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (output.TryGetBuffer(out var segment) && segment.Array is not null)
            {
                CryptographicOperations.ZeroMemory(
                    segment.Array.AsSpan(segment.Offset, segment.Count));
            }
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}
