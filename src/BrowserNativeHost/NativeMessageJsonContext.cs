using System.Text.Json.Serialization;
using Core.Passkeys;

namespace BrowserNativeHost;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NativeRequest))]
[JsonSerializable(typeof(NativeResponse))]
[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(BrowserRequestContext))]
public partial class NativeMessageJsonContext : JsonSerializerContext;
