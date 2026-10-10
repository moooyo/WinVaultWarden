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
        var sha1 = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        var prefix = sha1[..5];
        var suffix = sha1[5..];

        using var req = new HttpRequestMessage(HttpMethod.Get,
            new Uri($"https://api.pwnedpasswords.com/range/{prefix}"));
        req.Headers.Add("Add-Padding", "true");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        // Failed requests must remain distinguishable from a successful check with no matches.
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var hasValidRange = false;
        var hasMatchingSuffix = false;
        var breachCount = 0;
        foreach (var rawLine in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            if (line.Length < 37 || line[35] != ':'
                || !line.Take(35).All(char.IsAsciiHexDigit)
                || !int.TryParse(line.AsSpan(36), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                || count < 0)
                throw new InvalidDataException("HIBP response contained an invalid range record.");

            hasValidRange = true;
            if (line.AsSpan(0, 35).Equals(suffix, StringComparison.OrdinalIgnoreCase))
            {
                if (hasMatchingSuffix)
                    throw new InvalidDataException("HIBP response contained a duplicate range record.");
                hasMatchingSuffix = true;
                breachCount = count;
            }
        }
        if (!hasValidRange)
            throw new InvalidDataException("HIBP response did not contain any valid range records.");

        // Valid zero-count padding and valid ranges without a matching suffix are clean results.
        return breachCount;
    }
}
