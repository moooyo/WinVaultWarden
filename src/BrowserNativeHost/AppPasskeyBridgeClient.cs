using System.IO.Pipes;
using Core.BrowserIntegration;
using Core.Passkeys;

namespace BrowserNativeHost;

public interface IAppPasskeyBridgeClient : IAsyncDisposable
{
    Task<BrowserPasskeyResponse> SendAsync(BrowserPasskeyRequest request, CancellationToken ct = default);

    // Best-effort upstream cancellation for an in-flight request id. Safe to call for an unknown id.
    Task CancelAsync(string requestId, CancellationToken ct = default);
}

/// <summary>
/// Production bridge client: one Host lifetime keeps a single authenticated App pipe connection and
/// multiplexes every request id over it via <see cref="AppBridgeSession"/>. The connect factory owns
/// the Task 3 mutual handshake — verify the App server from the pipe kernel object, never a
/// self-reported PID, and exchange nonces before any passkey byte — and hands the post-handshake pipe
/// and session nonce to the multiplexer, which stamps the nonce on every request/cancel and checks it
/// on every response.
/// </summary>
public sealed class NamedPipeAppPasskeyBridgeClient : IAppPasskeyBridgeClient
{
    private static readonly TimeSpan BridgePhaseTimeout = TimeSpan.FromSeconds(2);

    // The App entry point ships next to the host (portable root), so its expected path is stable.
    private static readonly string AppExecutablePath =
        Path.Combine(AppContext.BaseDirectory, "App.exe");

    private readonly IPipePeerAuthorizer _peerAuthorizer;
    private readonly AppBridgeSession _session;

    public NamedPipeAppPasskeyBridgeClient()
        : this(new AppServerPeerAuthorizer())
    {
    }

    public NamedPipeAppPasskeyBridgeClient(IPipePeerAuthorizer peerAuthorizer)
    {
        _peerAuthorizer = peerAuthorizer;
        _session = new AppBridgeSession(ConnectAsync);
    }

    public Task<BrowserPasskeyResponse> SendAsync(BrowserPasskeyRequest request, CancellationToken ct = default) =>
        _session.SendAsync(request, ct);

    public Task CancelAsync(string requestId, CancellationToken ct = default) =>
        _session.CancelAsync(requestId, ct);

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    private async Task<AppBridgeConnection> ConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            BrowserPasskeyBridge.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectTimeout.CancelAfter(BridgePhaseTimeout);
                try
                {
                    await pipe.ConnectAsync(connectTimeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // This is the only fallback-enabling classification point: the pipe was never
                    // connected, so no App listener could have started this request.
                    throw new AppBridgeConnectFailedException(
                        new TimeoutException("Timed out connecting to the WinVaultWarden passkey bridge."));
                }
            }

            // Verify the server identity from the pipe kernel object, then exchange nonces — never
            // trust a self-reported PID, and never send anything before identity is established.
            PasskeyBridgeSession session;
            using (var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                handshakeTimeout.CancelAfter(BridgePhaseTimeout);
                try
                {
                    session = await PasskeyBridgeHandshake.CompleteClientAsync(
                        pipe,
                        (uint)Environment.ProcessId,
                        innerCt => _peerAuthorizer.AuthorizeServerAsync(pipe, AppExecutablePath, innerCt),
                        handshakeTimeout.Token);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    // A peer accepted the connection but did not complete the authenticated handshake.
                    // Treat that as an aborted/integrity failure, never as permission to fall back.
                    throw new IOException("Timed out completing the App passkey bridge handshake.", ex);
                }
            }

            return new AppBridgeConnection(pipe, session.Nonce, pipe);
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }
}
