using System.Text.Json;

namespace Api;

public static class AccessTokenSubjectReader
{
    public static bool TryReadAccountId(string? accessToken, out string accountId)
    {
        accountId = string.Empty;
        if (string.IsNullOrWhiteSpace(accessToken))
            return false;

        var parts = accessToken.Split('.');
        if (parts.Length != 3)
            return false;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(
                payload.Length + (4 - payload.Length % 4) % 4,
                '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!json.RootElement.TryGetProperty("sub", out var sub)
                || sub.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(sub.GetString()))
                return false;
            accountId = sub.GetString()!;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }
}
