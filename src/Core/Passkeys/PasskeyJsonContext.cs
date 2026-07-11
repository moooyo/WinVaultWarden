using System.Text.Json.Serialization;

namespace Core.Passkeys;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrowserPasskeyRequest))]
[JsonSerializable(typeof(BrowserPasskeyCancel))]
[JsonSerializable(typeof(BrowserPasskeyResponse))]
[JsonSerializable(typeof(PasskeyBridgeHello))]
[JsonSerializable(typeof(BrowserRequestContext))]
[JsonSerializable(typeof(PasskeyCreatePayload))]
[JsonSerializable(typeof(PasskeyGetPayload))]
[JsonSerializable(typeof(PasskeyGetAssertionPayload))]
[JsonSerializable(typeof(PasskeyCreateAttestationPayload))]
[JsonSerializable(typeof(ClientDataJson))]
[JsonSerializable(typeof(CredPropsResult))]
[JsonSerializable(typeof(CredPropsExtensions))]
public partial class PasskeyJsonContext : JsonSerializerContext;
