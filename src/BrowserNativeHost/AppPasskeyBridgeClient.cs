using System.IO.Pipes;
using Core.Passkeys;

namespace BrowserNativeHost;

public interface IAppPasskeyBridgeClient
{
    Task<BrowserPasskeyResponse> SendAsync(BrowserPasskeyRequest request, CancellationToken ct = default);
}

public sealed class NamedPipeAppPasskeyBridgeClient : IAppPasskeyBridgeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    public async Task<BrowserPasskeyResponse> SendAsync(BrowserPasskeyRequest request, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);

        await using var pipe = new NamedPipeClientStream(
            ".",
            BrowserPasskeyBridge.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out connecting to the WinVaultWarden passkey bridge.");
        }

        await BrowserPasskeyMessageProtocol.WriteAsync(pipe, request, PasskeyJsonContext.Default.BrowserPasskeyRequest, ct);
        return await BrowserPasskeyMessageProtocol.ReadAsync(pipe, PasskeyJsonContext.Default.BrowserPasskeyResponse, ct)
            ?? new BrowserPasskeyResponse(
                request.Id,
                "error",
                false,
                Error: new BrowserPasskeyError("empty_response", "WinVaultWarden returned an empty passkey response."));
    }
}
