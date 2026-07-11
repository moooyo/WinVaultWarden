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
        catch (Exception exception) when (
            exception is EndOfStreamException or IOException or InvalidDataException or JsonException)
        {
            // Stable transport note only: never the exception message/path (Task 7 sanitization).
            await error.WriteLineAsync("Native messaging transport terminated.");
            return 1;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Any other unexpected top-level failure (not a known transport fault, not OOM): the same
            // fixed, code-only stderr note. Never exception.Message/stack/path/token — this is the
            // last line of defence before the runtime's own unhandled-exception printer, which would
            // otherwise dump the full exception (Task 7 sanitization). OutOfMemoryException is
            // deliberately NOT matched here: a genuine out-of-memory condition must still crash the
            // process rather than be silently absorbed and reported as exit code 1.
            await error.WriteLineAsync("Native messaging host encountered an unexpected error.");
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
        catch (AppBridgeConnectFailedException)
        {
            // Never reached a connected App for this request: client_not_handled, the sole fallback
            // trigger (see NativeMessagingSession for the persistent-session equivalent).
            return Error(
                request,
                PasskeyErrorCodes.ClientNotHandled,
                "WinVaultWarden is not available to handle this request.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or UnauthorizedAccessException)
        {
            // A connection that WAS established is now lost mid-request: abort, never fall back.
            return Error(
                request,
                PasskeyErrorCodes.OperationAborted,
                "The App passkey bridge connection was lost.");
        }
    }

    // Code-only wire (Task 7): the message argument documents intent at the call site only.
    private static NativeResponse Error(NativeRequest request, string code, string message) =>
        new(request.Id, "error", false, Error: new NativeError(code));

    private static NativeError? ToNativeError(BrowserPasskeyError? error) =>
        error is null ? null : new NativeError(error.Code);
}
