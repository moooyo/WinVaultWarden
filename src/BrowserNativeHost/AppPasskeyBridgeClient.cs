using System.IO.Pipes;
using Core.BrowserIntegration;
using Core.Passkeys;

namespace BrowserNativeHost;

public interface IAppPasskeyBridgeClient
{
    Task<BrowserPasskeyResponse> SendAsync(BrowserPasskeyRequest request, CancellationToken ct = default);
}

public sealed class NamedPipeAppPasskeyBridgeClient : IAppPasskeyBridgeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    // The App entry point ships next to the host (portable root), so its expected path is stable.
    private static readonly string AppExecutablePath =
        Path.Combine(AppContext.BaseDirectory, "App.exe");

    private readonly IPipePeerAuthorizer _peerAuthorizer;

    public NamedPipeAppPasskeyBridgeClient()
        : this(new AppServerPeerAuthorizer())
    {
    }

    public NamedPipeAppPasskeyBridgeClient(IPipePeerAuthorizer peerAuthorizer)
    {
        _peerAuthorizer = peerAuthorizer;
    }

    public async Task<BrowserPasskeyResponse> SendAsync(BrowserPasskeyRequest request, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);

        await using var pipe = new NamedPipeClientStream(
            ".",
            BrowserPasskeyBridge.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        PasskeyBridgeSession session;
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            // Verify the server identity from the pipe kernel object, then exchange nonces — never
            // trust a self-reported PID, and never send anything before identity is established.
            session = await PasskeyBridgeHandshake.CompleteClientAsync(
                pipe,
                (uint)Environment.ProcessId,
                innerCt => _peerAuthorizer.AuthorizeServerAsync(pipe, AppExecutablePath, innerCt),
                timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out connecting to the WinVaultWarden passkey bridge.");
        }

        var bound = request with { Nonce = session.Nonce };
        await BrowserPasskeyMessageProtocol.WriteAsync(pipe, bound, PasskeyJsonContext.Default.BrowserPasskeyRequest, ct);
        var response = await BrowserPasskeyMessageProtocol.ReadAsync(pipe, PasskeyJsonContext.Default.BrowserPasskeyResponse, ct);
        if (response is null)
        {
            return new BrowserPasskeyResponse(
                request.Id,
                "error",
                false,
                Error: new BrowserPasskeyError("empty_response", "WinVaultWarden returned an empty passkey response."));
        }

        if (!PasskeyBridgeHandshake.NonceMatches(response.Nonce, session.Nonce))
            throw new UnauthorizedAccessException("Passkey bridge response nonce mismatch.");

        return response;
    }
}
