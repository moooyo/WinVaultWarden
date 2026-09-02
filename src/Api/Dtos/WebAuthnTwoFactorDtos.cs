using System.Text.Json;
using System.Text.Json.Serialization;

namespace Api.Dtos;

/// <summary>
/// Verifies a sensitive WebAuthn two-factor management operation.
/// Vaultwarden accepts either <see cref="MasterPasswordHash"/> or <see cref="Otp"/>.
/// Official servers also accept <see cref="AuthRequestAccessCode"/> for initial secret verification,
/// then require <see cref="UserVerificationToken"/> for WebAuthn continuation requests.
/// </summary>
public sealed record WebAuthnVerificationRequest
{
    [JsonPropertyName("masterPasswordHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MasterPasswordHash { get; }

    [JsonPropertyName("otp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Otp { get; }

    [JsonPropertyName("authRequestAccessCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthRequestAccessCode { get; }

    [JsonPropertyName("userVerificationToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserVerificationToken { get; }

    [JsonConstructor]
    public WebAuthnVerificationRequest(
        string? masterPasswordHash,
        string? otp,
        string? authRequestAccessCode,
        string? userVerificationToken)
    {
        ValidateSelection(masterPasswordHash, otp, authRequestAccessCode, userVerificationToken);
        MasterPasswordHash = masterPasswordHash;
        Otp = otp;
        AuthRequestAccessCode = authRequestAccessCode;
        UserVerificationToken = userVerificationToken;
    }

    public static WebAuthnVerificationRequest FromMasterPasswordHash(string value) =>
        new(RequireValue(value), null, null, null);

    public static WebAuthnVerificationRequest FromOtp(string value) =>
        new(null, RequireValue(value), null, null);

    public static WebAuthnVerificationRequest FromAuthRequestAccessCode(string value) =>
        new(null, null, RequireValue(value), null);

    public static WebAuthnVerificationRequest FromUserVerificationToken(string value) =>
        new(null, null, null, RequireValue(value));

    internal static void ValidateSelection(params string?[] values)
    {
        var selectedCount = values.Count(static value => value is not null);
        var selected = values.FirstOrDefault(static value => value is not null);
        if (selectedCount != 1 || string.IsNullOrWhiteSpace(selected))
        {
            throw new ArgumentException("Exactly one WebAuthn verification field must be provided.");
        }
    }

    internal static string RequireValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}

/// <summary>
/// A server-side WebAuthn two-factor credential summary. Private key material remains in the authenticator.
/// </summary>
public sealed record WebAuthnCredentialItem(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("migrated")] bool Migrated);

/// <summary>
/// Normalized provider details. The converter accepts Vaultwarden's flat response and the official wrapper.
/// </summary>
[JsonConverter(typeof(WebAuthnTwoFactorResponseJsonConverter))]
public sealed record WebAuthnTwoFactorResponse(
    bool Enabled,
    IReadOnlyList<WebAuthnCredentialItem> Keys,
    string? UserVerificationToken = null,
    string? Object = null);

/// <summary>
/// Normalized creation options. Vaultwarden returns them directly; official servers use an options wrapper.
/// </summary>
[JsonConverter(typeof(WebAuthnChallengeResponseJsonConverter))]
public sealed record WebAuthnChallengeResponse(JsonElement? Options);

public sealed record WebAuthnAttestationResponse(
    [property: JsonPropertyName("AttestationObject")] string AttestationObject,
    [property: JsonPropertyName("clientDataJson")] string ClientDataJson,
    [property: JsonPropertyName("transports")] string[] Transports);

public sealed record WebAuthnRegistrationCredential(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("rawId")] string RawId,
    [property: JsonPropertyName("response")] WebAuthnAttestationResponse Response,
    [property: JsonPropertyName("type")] string Type = "public-key")
{
    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record EnableWebAuthnRequest
{
    [JsonPropertyName("id")]
    public int Id { get; }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("deviceResponse")]
    public WebAuthnRegistrationCredential DeviceResponse { get; }

    [JsonPropertyName("masterPasswordHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MasterPasswordHash { get; }

    [JsonPropertyName("otp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Otp { get; }

    [JsonPropertyName("authRequestAccessCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthRequestAccessCode { get; }

    [JsonPropertyName("userVerificationToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserVerificationToken { get; }

    [JsonConstructor]
    public EnableWebAuthnRequest(
        int id,
        string name,
        WebAuthnRegistrationCredential deviceResponse,
        string? masterPasswordHash,
        string? otp,
        string? authRequestAccessCode,
        string? userVerificationToken)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(deviceResponse);
        WebAuthnVerificationRequest.ValidateSelection(
            masterPasswordHash,
            otp,
            authRequestAccessCode,
            userVerificationToken);

        Id = id;
        Name = name;
        DeviceResponse = deviceResponse;
        MasterPasswordHash = masterPasswordHash;
        Otp = otp;
        AuthRequestAccessCode = authRequestAccessCode;
        UserVerificationToken = userVerificationToken;
    }

    public static EnableWebAuthnRequest Create(
        int id,
        string name,
        WebAuthnRegistrationCredential deviceResponse,
        WebAuthnVerificationRequest verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        return new EnableWebAuthnRequest(
            id,
            name,
            deviceResponse,
            verification.MasterPasswordHash,
            verification.Otp,
            verification.AuthRequestAccessCode,
            verification.UserVerificationToken);
    }
}

public sealed record DeleteWebAuthnCredentialRequest
{
    [JsonPropertyName("id")]
    public int Id { get; }

    [JsonPropertyName("masterPasswordHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MasterPasswordHash { get; }

    [JsonPropertyName("otp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Otp { get; }

    [JsonPropertyName("authRequestAccessCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthRequestAccessCode { get; }

    [JsonPropertyName("userVerificationToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserVerificationToken { get; }

    [JsonConstructor]
    public DeleteWebAuthnCredentialRequest(
        int id,
        string? masterPasswordHash,
        string? otp,
        string? authRequestAccessCode,
        string? userVerificationToken)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        WebAuthnVerificationRequest.ValidateSelection(
            masterPasswordHash,
            otp,
            authRequestAccessCode,
            userVerificationToken);

        Id = id;
        MasterPasswordHash = masterPasswordHash;
        Otp = otp;
        AuthRequestAccessCode = authRequestAccessCode;
        UserVerificationToken = userVerificationToken;
    }

    public static DeleteWebAuthnCredentialRequest Create(
        int id,
        WebAuthnVerificationRequest verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        return new DeleteWebAuthnCredentialRequest(
            id,
            verification.MasterPasswordHash,
            verification.Otp,
            verification.AuthRequestAccessCode,
            verification.UserVerificationToken);
    }
}

public sealed record DeleteAllWebAuthnCredentialsRequest
{
    [JsonPropertyName("userVerificationToken")]
    public string UserVerificationToken { get; }

    [JsonConstructor]
    public DeleteAllWebAuthnCredentialsRequest(string userVerificationToken)
    {
        UserVerificationToken = WebAuthnVerificationRequest.RequireValue(userVerificationToken);
    }

    public static DeleteAllWebAuthnCredentialsRequest FromUserVerificationToken(string value) =>
        new(value);
}

internal sealed class WebAuthnTwoFactorResponseJsonConverter : JsonConverter<WebAuthnTwoFactorResponse>
{
    public override WebAuthnTwoFactorResponse Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("The WebAuthn provider response must be an object.");

        var details = root;
        if (TryGetProperty(root, "webAuthn", out var wrapped))
        {
            if (wrapped.ValueKind != JsonValueKind.Object)
                throw new JsonException("The WebAuthn provider wrapper is invalid.");
            details = wrapped;
        }

        if (!TryGetProperty(details, "enabled", out var enabledElement)
            || enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JsonException("The WebAuthn provider enabled state is invalid.");
        }
        var enabled = enabledElement.GetBoolean();

        var keys = new List<WebAuthnCredentialItem>();
        var hasKeys = TryGetProperty(details, "keys", out var keysElement);
        if (!hasKeys && enabled)
            throw new JsonException("The enabled WebAuthn credential list is missing.");
        if (hasKeys && keysElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var key in keysElement.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.Object)
                    throw new JsonException("A WebAuthn credential entry is invalid.");

                if (!TryGetProperty(key, "id", out var idElement)
                    || !TryReadInt32(idElement, out var id))
                {
                    throw new JsonException("A WebAuthn credential identifier is invalid.");
                }

                var name = string.Empty;
                if (TryGetProperty(key, "name", out var nameElement)
                    && nameElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                {
                    if (nameElement.ValueKind != JsonValueKind.String)
                        throw new JsonException("A WebAuthn credential name is invalid.");
                    name = nameElement.GetString() ?? string.Empty;
                }

                var migrated = false;
                if (TryGetProperty(key, "migrated", out var migratedElement)
                    && migratedElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                {
                    if (migratedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new JsonException("A WebAuthn credential migration state is invalid.");
                    migrated = migratedElement.GetBoolean();
                }
                keys.Add(new WebAuthnCredentialItem(id, name, migrated));
            }
        }
        else if (hasKeys && keysElement.ValueKind is not JsonValueKind.Null)
        {
            throw new JsonException("The WebAuthn credential list is invalid.");
        }

        string? verificationToken = null;
        if (TryGetProperty(root, "userVerificationToken", out var tokenElement)
            && tokenElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (tokenElement.ValueKind != JsonValueKind.String)
                throw new JsonException("The WebAuthn user verification token is invalid.");
            verificationToken = tokenElement.GetString();
        }

        string? objectName = null;
        if (TryGetProperty(details, "object", out var objectElement)
            && objectElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (objectElement.ValueKind != JsonValueKind.String)
                throw new JsonException("The WebAuthn response object type is invalid.");
            objectName = objectElement.GetString();
        }

        return new WebAuthnTwoFactorResponse(enabled, keys, verificationToken, objectName);
    }

    public override void Write(
        Utf8JsonWriter writer,
        WebAuthnTwoFactorResponse value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", value.Enabled);
        writer.WritePropertyName("keys");
        writer.WriteStartArray();
        foreach (var key in value.Keys)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", key.Id);
            writer.WriteString("name", key.Name);
            writer.WriteBoolean("migrated", key.Migrated);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (value.UserVerificationToken is not null)
            writer.WriteString("userVerificationToken", value.UserVerificationToken);
        if (value.Object is not null)
            writer.WriteString("object", value.Object);
        writer.WriteEndObject();
    }

    private static bool TryReadInt32(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt32(out value);
        if (element.ValueKind == JsonValueKind.String)
            return int.TryParse(element.GetString(), out value);
        value = 0;
        return false;
    }

    internal static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}

internal sealed class WebAuthnChallengeResponseJsonConverter : JsonConverter<WebAuthnChallengeResponse>
{
    public override WebAuthnChallengeResponse Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("The WebAuthn challenge response must be an object.");
        if (WebAuthnTwoFactorResponseJsonConverter.TryGetProperty(root, "options", out var wrapped))
        {
            if (wrapped.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
                throw new JsonException("The WebAuthn challenge wrapper is invalid.");
            return new WebAuthnChallengeResponse(
                wrapped.ValueKind == JsonValueKind.Null ? null : wrapped.Clone());
        }

        return new WebAuthnChallengeResponse(root.Clone());
    }

    public override void Write(
        Utf8JsonWriter writer,
        WebAuthnChallengeResponse value,
        JsonSerializerOptions options)
    {
        if (value.Options is { } challenge)
            challenge.WriteTo(writer);
        else
            writer.WriteNullValue();
    }
}
