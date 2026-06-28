using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Core.Passkeys;

public static class BrowserPasskeyBridge
{
    public const string PipeName = "WinVaultWarden.PasskeyBridge";
    public const int MaxMessageBytes = 1024 * 1024;
}

public sealed record BrowserPasskeyRequest(
    string? Id,
    string? Type,
    JsonElement? Payload = null);

public sealed record BrowserPasskeyResponse(
    string? Id,
    string Type,
    bool Ok,
    JsonElement? Payload = null,
    BrowserPasskeyError? Error = null);

public sealed record BrowserPasskeyError(string Code, string Message);

public sealed record PasskeyCreatePayload(
    string Origin,
    string? RpId,
    string Challenge,
    PasskeyRelyingParty? Rp,
    PasskeyUserEntity? User,
    PasskeyCredentialParameter[] PubKeyCredParams,
    PasskeyCredentialDescriptor[] ExcludeCredentials,
    JsonElement? AuthenticatorSelection,
    JsonElement? Extensions,
    string? Attestation,
    int? Timeout);

public sealed record PasskeyGetPayload(
    string Origin,
    string? RpId,
    string Challenge,
    PasskeyCredentialDescriptor[] AllowCredentials,
    string? UserVerification,
    string? Mediation,
    int? Timeout);

public sealed record PasskeyRelyingParty(string? Id, string? Name);

public sealed record PasskeyUserEntity(string Id, string? Name, string? DisplayName);

public sealed record PasskeyCredentialParameter(string Type, int Alg);

public sealed record PasskeyCredentialDescriptor(string Id, string? Type, string[] Transports);

public sealed record ParsedPasskeyRequest(string Id, string Type, object Payload);

public sealed record PasskeyGetAssertionPayload(
    [property: JsonPropertyName("credentialId")] string CredentialId,
    [property: JsonPropertyName("authenticatorData")] string AuthenticatorData,
    [property: JsonPropertyName("clientDataJSON")] string ClientDataJson,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("userHandle")] string? UserHandle);

public static class PasskeyRequestParser
{
    public static bool TryParse(
        BrowserPasskeyRequest request,
        out ParsedPasskeyRequest? parsed,
        out BrowserPasskeyError? error)
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(request.Id))
        {
            error = new BrowserPasskeyError("invalid_request", "Passkey request id is required.");
            return false;
        }

        if (request.Payload is null)
        {
            error = new BrowserPasskeyError("invalid_request", "Passkey request payload is required.");
            return false;
        }

        try
        {
            return request.Type switch
            {
                "passkey.create" => TryParseCreate(request, out parsed, out error),
                "passkey.get" => TryParseGet(request, out parsed, out error),
                _ => Fail("unsupported_passkey_type", "Unsupported passkey request type.", out parsed, out error),
            };
        }
        catch (JsonException)
        {
            error = new BrowserPasskeyError("invalid_request", "Passkey request payload shape is invalid.");
            return false;
        }
    }

    private static bool TryParseCreate(
        BrowserPasskeyRequest request,
        out ParsedPasskeyRequest? parsed,
        out BrowserPasskeyError? error)
    {
        var payload = request.Payload!.Value.Deserialize(PasskeyJsonContext.Default.PasskeyCreatePayload);
        if (payload is null)
            return Fail("invalid_request", "Passkey create payload is required.", out parsed, out error);

        if (!ValidateOrigin(payload.Origin, out error))
        {
            parsed = null;
            return false;
        }

        if (!ValidateBase64Url(payload.Challenge, "challenge", out error)
            || !ValidateBase64Url(payload.User?.Id, "user.id", out error))
        {
            parsed = null;
            return false;
        }

        var credentialParameters = payload.PubKeyCredParams ?? [];
        if (credentialParameters.Length == 0)
        {
            return Fail(
                "invalid_request",
                "Passkey create request must include public key credential parameters.",
                out parsed,
                out error);
        }

        parsed = new ParsedPasskeyRequest(request.Id!, request.Type!, payload);
        error = null;
        return true;
    }

    private static bool TryParseGet(
        BrowserPasskeyRequest request,
        out ParsedPasskeyRequest? parsed,
        out BrowserPasskeyError? error)
    {
        var payload = request.Payload!.Value.Deserialize(PasskeyJsonContext.Default.PasskeyGetPayload);
        if (payload is null)
            return Fail("invalid_request", "Passkey get payload is required.", out parsed, out error);

        if (!ValidateOrigin(payload.Origin, out error)
            || !ValidateBase64Url(payload.Challenge, "challenge", out error))
        {
            parsed = null;
            return false;
        }

        foreach (var credential in payload.AllowCredentials ?? [])
        {
            if (!ValidateBase64Url(credential.Id, "allowCredentials.id", out error))
            {
                parsed = null;
                return false;
            }
        }

        parsed = new ParsedPasskeyRequest(request.Id!, request.Type!, payload);
        error = null;
        return true;
    }

    private static bool ValidateOrigin(string? origin, out BrowserPasskeyError? error)
    {
        if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = new BrowserPasskeyError("invalid_request", "Passkey request origin must be an absolute origin.");
            return false;
        }

        error = null;
        return true;
    }

    private static bool ValidateBase64Url(string? value, string fieldName, out BrowserPasskeyError? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = new BrowserPasskeyError("invalid_request", $"Passkey request {fieldName} is required.");
            return false;
        }

        try
        {
            DecodeBase64Url(value);
            error = null;
            return true;
        }
        catch (FormatException)
        {
            error = new BrowserPasskeyError("invalid_request", $"Passkey request {fieldName} must be base64url.");
            return false;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Convert.FromBase64String(base64);
    }

    private static bool Fail(
        string code,
        string message,
        out ParsedPasskeyRequest? parsed,
        out BrowserPasskeyError? error)
    {
        parsed = null;
        error = new BrowserPasskeyError(code, message);
        return false;
    }
}

public static class BrowserPasskeyMessageProtocol
{
    public static async Task<T?> ReadAsync<T>(Stream input, JsonTypeInfo<T> typeInfo, CancellationToken ct = default)
    {
        var lengthBytes = new byte[4];
        var read = await ReadExactOrEndAsync(input, lengthBytes, lengthBytes.Length, ct);
        if (read == 0)
            return default;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (length > BrowserPasskeyBridge.MaxMessageBytes)
            throw new InvalidDataException($"Passkey bridge message is too large: {length} bytes.");

        var payload = new byte[length];
        await ReadExactOrEndAsync(input, payload, payload.Length, ct);
        return JsonSerializer.Deserialize(payload, typeInfo);
    }

    public static async Task WriteAsync<T>(Stream output, T message, JsonTypeInfo<T> typeInfo, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
        if (payload.Length > BrowserPasskeyBridge.MaxMessageBytes)
            throw new InvalidDataException($"Passkey bridge message is too large: {payload.Length} bytes.");

        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)payload.Length);
        await output.WriteAsync(lengthBytes, ct);
        await output.WriteAsync(payload, ct);
        await output.FlushAsync(ct);
    }

    private static async Task<int> ReadExactOrEndAsync(Stream input, byte[] buffer, int length, CancellationToken ct)
    {
        var total = 0;
        while (total < length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total, length - total), ct);
            if (read == 0)
            {
                if (total == 0)
                    return 0;

                throw new EndOfStreamException("Unexpected end of passkey bridge message stream.");
            }

            total += read;
        }

        return total;
    }
}
