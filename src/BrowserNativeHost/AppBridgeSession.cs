using System.Collections.Concurrent;
using Core.Passkeys;

namespace BrowserNativeHost;

// Transport for one App pipe connection: the post-handshake duplex stream, the session nonce, and a
// resource handle disposed on teardown. Produced by a connect factory so the pipe + Task 3 handshake
// stay in NamedPipeAppPasskeyBridgeClient while the multiplexing here is transport-agnostic and
// unit-testable over an in-memory duplex.
public sealed record AppBridgeConnection(Stream Stream, string Nonce, IAsyncDisposable Resource);

/// <summary>
/// One Host lifetime keeps a single App pipe connection and multiplexes every concurrent request id
/// over it. A background read loop routes each response to its pending id, so responses may arrive out
/// of order; all writes share one lock so frames never interleave. A duplicate id is rejected locally
/// with <see cref="PasskeyErrorCodes.RequestInProgress"/>; cancelling a request removes its pending
/// slot and forwards a nonce-bound cancel envelope upstream; a disconnect or a nonce-mismatched
/// response faults the connection and fails every pending request, after which the next request
/// transparently reconnects. Every request and cancel carries the session nonce and every response is
/// nonce-checked before it can satisfy a caller.
/// </summary>
public sealed class AppBridgeSession : IAppPasskeyBridgeClient
{
    private readonly Func<CancellationToken, Task<AppBridgeConnection>> _connect;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserPasskeyResponse>> _pending =
        new(StringComparer.Ordinal);
    private Connection? _connection;
    private int _disposed;

    public AppBridgeSession(Func<CancellationToken, Task<AppBridgeConnection>> connect) =>
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));

    public async Task<BrowserPasskeyResponse> SendAsync(BrowserPasskeyRequest request, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(request.Id);

        var connection = await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var completion = new TaskCompletionSource<BrowserPasskeyResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.Id, completion))
            return BrowserPasskeyResponse.Failure(request.Id, PasskeyErrorCodes.RequestInProgress);

        using var registration = ct.Register(() =>
        {
            if (_pending.TryRemove(request.Id, out var pending))
                pending.TrySetCanceled(ct);
        });

        try
        {
            await WriteAsync(connection, request with { Nonce = connection.Nonce }, ct).ConfigureAwait(false);
        }
        catch
        {
            // The frame never left (or only its gate wait was cancelled); drop the slot and surface it.
            _pending.TryRemove(request.Id, out _);
            throw;
        }

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller walked away: tell the App to abandon the ceremony, then propagate the cancel.
            await CancelAsync(request.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CancelAsync(string requestId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(requestId))
            return;

        var connection = Volatile.Read(ref _connection);
        if (connection is null || connection.Faulted)
            return;

        try
        {
            await WriteAsync(connection, new BrowserPasskeyCancel(requestId, Nonce: connection.Nonce), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: the pending slot is already gone, so a failed cancel write changes nothing.
        }
    }

    private async Task<Connection> EnsureConnectedAsync(CancellationToken ct)
    {
        var current = Volatile.Read(ref _connection);
        if (current is { Faulted: false })
            return current;

        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            current = _connection;
            if (current is { Faulted: false })
                return current;

            if (current is not null)
            {
                await current.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }

            var transport = await _connect(ct).ConfigureAwait(false);
            var connection = new Connection(transport);
            connection.ReadLoop = ReadLoopAsync(connection);
            Volatile.Write(ref _connection, connection);
            return connection;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task ReadLoopAsync(Connection connection)
    {
        // Default teardown reason; a nonce mismatch upgrades it to a fail-closed authorization error.
        Exception failure = new IOException("App bridge disconnected.");
        try
        {
            while (true)
            {
                BrowserPasskeyResponse? response;
                try
                {
                    response = await BrowserPasskeyMessageProtocol.ReadAsync(
                        connection.Stream,
                        PasskeyJsonContext.Default.BrowserPasskeyResponse,
                        connection.ReadCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
                {
                    break;
                }

                if (response is null)
                    break;

                if (!PasskeyBridgeHandshake.NonceMatches(response.Nonce, connection.Nonce))
                {
                    failure = new UnauthorizedAccessException("Passkey bridge response nonce mismatch.");
                    break;
                }

                if (response.Id is not null && _pending.TryRemove(response.Id, out var pending))
                    pending.TrySetResult(response with { Nonce = null });
            }
        }
        finally
        {
            connection.Faulted = true;
            FailAllPending(failure);
        }
    }

    private async Task WriteAsync(Connection connection, BrowserPasskeyRequest request, CancellationToken ct)
    {
        // The gate wait is cancellable, but the frame itself is written with None so a cancel can never
        // leave a half-written frame that would desync the peer.
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await BrowserPasskeyMessageProtocol.WriteAsync(
                connection.Stream, request, PasskeyJsonContext.Default.BrowserPasskeyRequest, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WriteAsync(Connection connection, BrowserPasskeyCancel cancel, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await BrowserPasskeyMessageProtocol.WriteAsync(
                connection.Stream, cancel, PasskeyJsonContext.Default.BrowserPasskeyCancel, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void FailAllPending(Exception exception)
    {
        foreach (var id in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(id, out var pending))
                pending.TrySetException(exception);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(AppBridgeSession));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
            await connection.DisposeAsync().ConfigureAwait(false);

        FailAllPending(new ObjectDisposedException(nameof(AppBridgeSession)));
        _writeGate.Dispose();
        _connectGate.Dispose();
    }

    private sealed class Connection : IAsyncDisposable
    {
        private readonly AppBridgeConnection _transport;

        public Connection(AppBridgeConnection transport) => _transport = transport;

        public Stream Stream => _transport.Stream;
        public string Nonce => _transport.Nonce;
        public CancellationTokenSource ReadCts { get; } = new();
        public Task ReadLoop { get; set; } = Task.CompletedTask;
        public volatile bool Faulted;

        public async ValueTask DisposeAsync()
        {
            ReadCts.Cancel();
            try
            {
                await ReadLoop.ConfigureAwait(false);
            }
            catch
            {
                // The read loop only ever completes here; its failure is already fanned out to callers.
            }

            await _transport.Resource.DisposeAsync().ConfigureAwait(false);
            ReadCts.Dispose();
        }
    }
}
