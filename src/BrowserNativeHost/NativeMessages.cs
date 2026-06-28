using System.Text.Json;

namespace BrowserNativeHost;

public sealed record NativeRequest(
    string? Id,
    string? Type,
    JsonElement? Payload = null);

public sealed record NativeResponse(
    string? Id,
    string Type,
    bool Ok,
    JsonElement? Payload = null,
    NativeError? Error = null);

public sealed record NativeError(string Code, string Message);

public sealed record HostInfo(string Name, string Version);
