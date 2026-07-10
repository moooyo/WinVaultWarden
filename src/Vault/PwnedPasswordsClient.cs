using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vault;

public interface IPwnedPasswordsClient
{
    Task<int> GetBreachCountAsync(string password, CancellationToken ct = default);
}

public sealed class PwnedPasswordsClient : IPwnedPasswordsClient
{
    private readonly HttpClient _http;
    public PwnedPasswordsClient(HttpClient http) => _http = http;

    public async Task<int> GetBreachCountAsync(string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password)) return 0;
        var sha1 = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password))); // 大写 hex
        var prefix = sha1[..5];
        var suffix = sha1[5..];

        using var req = new HttpRequestMessage(HttpMethod.Get,
            new Uri($"https://api.pwnedpasswords.com/range/{prefix}"));
        req.Headers.Add("Add-Padding", "true");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        // 429/5xx/网络失败必须抛给调用方,绝不能悄悄折算为零命中。
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(':');
            if (parts.Length == 2 && string.Equals(parts[0], suffix, StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(
                    parts[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var count)
                    ? count
                    : throw new InvalidDataException(
                        "HIBP response contained an invalid breach count.");
            }
        }
        // 成功 200 响应中未命中该 suffix:确定未泄露,返回零。
        return 0;
    }
}
