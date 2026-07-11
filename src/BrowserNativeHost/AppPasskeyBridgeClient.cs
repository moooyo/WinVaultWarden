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
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);

        var pipe = new NamedPipeClientStream(
            ".",
            BrowserPasskeyBridge.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(timeout.Token);
            // Verify the server identity from the pipe kernel object, then exchange nonces — never
            // trust a self-reported PID, and never send anything before identity is established.
            var session = await PasskeyBridgeHandshake.CompleteClientAsync(
                pipe,
                (uint)Environment.ProcessId,
                innerCt => _peerAuthorizer.AuthorizeServerAsync(pipe, AppExecutablePath, innerCt),
                timeout.Token);
            return new AppBridgeConnection(pipe, session.Nonce, pipe);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await pipe.DisposeAsync();
            throw new TimeoutException("Timed out connecting to the WinVaultWarden passkey bridge.");
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }
}
