using Core.Abstractions;

namespace Api;

// 网络层占位。骨架阶段仅持有 HttpClient 与基址,不发真实请求。
public sealed class ApiClient : IApiClient
{
    private readonly HttpClient _http;
    private string _baseUrl = string.Empty;

    public ApiClient(HttpClient http) => _http = http;

    public void SetBaseAddress(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http.BaseAddress = new Uri(_baseUrl);
    }
}
