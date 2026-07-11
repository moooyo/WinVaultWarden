using System.Text.Json;
using Core.Passkeys;

namespace BrowserNativeHost;

public static class NativeMessagingHost
{
    public const string HostName = "WinVaultWarden.NativeHost";
    public const string HostVersion = "0.1.0";

    public static async Task<int> RunAsync(
        Stream input,
        Stream output,
        TextWriter error,
        CancellationToken ct = default,
        IAppPasskeyBridgeClient? appBridge = null)
    {
        // Own the default bridge so its persistent App connection is disposed when the host exits;
        // a caller-supplied bridge stays owned by the caller.
        var ownedBridge = appBridge is null ? new NamedPipeAppPasskeyBridgeClient() : null;
        var bridge = appBridge ?? ownedBridge!;
        await using var session = new NativeMessagingSession(input, output, bridge, TimeProvider.System);
        try
        {
            await session.RunAsync(ct);
            return 0;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or InvalidDataException or JsonException)
        {
            await error.WriteLineAsync($"Native messaging host failed: {ex.Message}");
            return 1;
        }
        finally
        {
            if (ownedBridge is not null)
                await ownedBridge.DisposeAsync();
        }
    }

    public static NativeResponse Handle(NativeRequest request)
    {
        return HandleAsync(request, new NamedPipeAppPasskeyBridgeClient(), CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    public static Task<NativeResponse> HandleAsync(
        NativeRequest request,
        IAppPasskeyBridgeClient? appBridge = null,
        CancellationToken ct = default)
    {
        return request.Type switch
        {
            "ping" => Task.FromResult(new NativeResponse(
                request.Id,
                "pong",
                true,
                JsonSerializer.SerializeToElement(
                    new HostInfo(HostName, HostVersion),
                    NativeMessageJsonContext.Default.HostInfo))),
            "passkey.create" or "passkey.get" => HandlePasskeyRequestAsync(
                request,
                appBridge ?? new NamedPipeAppPasskeyBridgeClient(),
                ct),
            null or "" => Task.FromResult(Error(request, "missing_type", "Native message type is required.")),
            _ => Task.FromResult(Error(request, "unknown_type", $"Unsupported native message type: {request.Type}")),
        };
    }

    private static async Task<NativeResponse> HandlePasskeyRequestAsync(
        NativeRequest request,
        IAppPasskeyBridgeClient appBridge,
        CancellationToken ct)
    {
        var bridgeRequest = new BrowserPasskeyRequest(
            request.Id,
            request.Type,
            request.Payload,
            DeadlineUnixMs: request.DeadlineUnixMs,
            Context: request.Context);
        if (!PasskeyRequestParser.TryParse(bridgeRequest, out _, out var error))
            return new NativeResponse(request.Id, "error", false, Error: ToNativeError(error));

        try
        {
            var response = await appBridge.SendAsync(bridgeRequest, ct);
            return new NativeResponse(
                response.Id ?? request.Id,
                response.Type,
                response.Ok,
                response.Payload,
                ToNativeError(response.Error));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error(
                request,
                "native_host_unavailable",
                "WinVaultWarden is not running or its passkey bridge is unavailable.");
        }
    }

    private static NativeResponse Error(NativeRequest request, string code, string message) =>
        new(request.Id, "error", false, Error: new NativeError(code, message));

    private static NativeError? ToNativeError(BrowserPasskeyError? error) =>
        error is null ? null : new NativeError(error.Code, error.Message);
}
