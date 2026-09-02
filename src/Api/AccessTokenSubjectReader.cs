using System.Text.Json;

namespace Api;

public static class AccessTokenSubjectReader
{
    public static bool TryReadAccountId(string? accessToken, out string accountId)
        => TryReadStringClaim(accessToken, "sub", out accountId);

    public static bool TryReadEmail(string? accessToken, out string email)
        => TryReadStringClaim(accessToken, "email", out email);

    private static bool TryReadStringClaim(
        string? accessToken,
        string claimName,
        out string value)
    {
        value = string.Empty;
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
            if (!json.RootElement.TryGetProperty(claimName, out var claim)
                || claim.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(claim.GetString()))
                return false;
            value = claim.GetString()!;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }
}
