using System.Text.Json;
using Core.Passkeys;

namespace BrowserNativeHost;

public sealed record NativeRequest(
    string? Id,
    string? Type,
    JsonElement? Payload = null,
    // Absolute wall-clock deadline (unix epoch milliseconds) supplied by the extension. The session
    // clamps it to the hard cap and fails a request that is already past due; null falls back to the
    // default deadline.
    long? DeadlineUnixMs = null,
    // Trusted sender-supplied frame/RP context (never page-reported). Forwarded verbatim to the App
    // so the policy derives origin and RP identity from it rather than from the page payload.
    BrowserRequestContext? Context = null);

public sealed record NativeResponse(
    string? Id,
    string Type,
    bool Ok,
    JsonElement? Payload = null,
    NativeError? Error = null);

public sealed record NativeError(string Code, string Message);

public sealed record HostInfo(string Name, string Version);
