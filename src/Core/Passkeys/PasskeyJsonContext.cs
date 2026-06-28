using System.Text.Json.Serialization;

namespace Core.Passkeys;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrowserPasskeyRequest))]
[JsonSerializable(typeof(BrowserPasskeyResponse))]
[JsonSerializable(typeof(PasskeyCreatePayload))]
[JsonSerializable(typeof(PasskeyGetPayload))]
[JsonSerializable(typeof(PasskeyGetAssertionPayload))]
[JsonSerializable(typeof(ClientDataJson))]
public partial class PasskeyJsonContext : JsonSerializerContext;
