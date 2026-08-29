using System.Collections.Concurrent;
using Core.Passkeys;

namespace BrowserNativeHost;

// Transport for one App pipe connection: the post-handshake duplex stream, the session nonce, and a
// resource handle disposed on teardown. Produced by a connect factory so the pipe + Task 3 handshake
// stay in NamedPipeAppPasskeyBridgeClient while the multiplexing here is transport-agnostic and
// unit-testable over an in-memory duplex.
public sealed record AppBridgeConnection(Stream Stream, string Nonce, IAsyncDisposable Resource);

// Raised only by the production named-pipe client at the exact point where pipe connection times out
// before any App listener accepts the request. The generic session never infers this state from an
// exception type, so handshake, authorization, framing, and integrity failures cannot enable fallback.
public sealed class AppBridgeConnectFailedException : Exception
{
    public AppBridgeConnectFailedException(Exception inner)
        : base("Failed to establish the App passkey bridge connection.", inner)
    {
    }
}

/// <summary>
/// One Host lifetime keeps a single App pipe connection and multiplexes every concurrent request id
/// over it. A background read loop routes each response to its pending id, so responses may arrive out
/// of order; all writes share one lock so frames never interleave. A duplicate id is rejected locally
/// with <see cref="PasskeyErrorCodes.RequestInProgress"/>; cancelling a request removes its pending
/// slot and forwards a nonce-bound cancel envelope upstream; a disconnect or a nonce-mismatched
/// response faults the connection and fails every pending request, after which the next request
/// transparently reconnects. Every request and cancel carries the session nonce and every response is
/// nonce-checked before it can satisfy a caller. At most <see cref="MaxPendingRequests"/> requests may
/// await App responses; excess work fails locally with <see cref="PasskeyErrorCodes.RateLimited"/>.
/// </summary>
public sealed class AppBridgeSession : IAppPasskeyBridgeClient
{
    public const int MaxPendingRequests = 8;

    private readonly Func<CancellationToken, Task<AppBridgeConnection>> _connect;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _pendingSlots = new(MaxPendingRequests, MaxPendingRequests);
    private readonly ConcurrentDictionary<string, PendingEntry> _pending =
        new(StringComparer.Ordinal);
    private readonly AppBridgeSessionTestHooks? _testHooks;
    private Connection? _connection;
    private int _disposed;

    public AppBridgeSession(Func<CancellationToken, Task<AppBridgeConnection>> connect)
        : this(connect, null)
    {
    }

    internal AppBridgeSession(
        Func<CancellationToken, Task<AppBridgeConnection>> connect,
        AppBridgeSessionTestHooks? testHooks)
    {
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _testHooks = testHooks;
    }

    public async Task<BrowserPasskeyResponse> SendAsync(BrowserPasskeyRequest request, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(request.Id);

        if (_pending.ContainsKey(request.Id))
            return BrowserPasskeyResponse.Failure(request.Id, PasskeyErrorCodes.RequestInProgress);

        if (!_pendingSlots.Wait(0))
            return BrowserPasskeyResponse.Failure(request.Id, PasskeyErrorCodes.RateLimited);

        var slotReserved = true;
        try
        {
            _testHooks?.PendingSlotReserved?.Invoke(request.Id);

            while (true)
            {
                var connection = await EnsureConnectedAsync(ct).ConfigureAwait(false);
                var completion = new TaskCompletionSource<BrowserPasskeyResponse>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var entry = new PendingEntry(connection, completion);
                CancellationTokenRegistration registration = default;
                var registered = false;

                try
                {
                    await connection.LifecycleGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        // The read loop marks Faulted before waiting for this gate. A sender that
                        // observed the old generation just before that transition retries instead of
                        // registering work that its completed read loop can never answer.
                        if (connection.Faulted || !ReferenceEquals(connection, Volatile.Read(ref _connection)))
                            continue;

                        if (!_pending.TryAdd(request.Id, entry))
                            return BrowserPasskeyResponse.Failure(request.Id, PasskeyErrorCodes.RequestInProgress);

                        slotReserved = false;
                        registration = ct.Register(() =>
                        {
                            if (TryTakePending(request.Id, entry, out var cancelled))
                                cancelled.Completion.TrySetCanceled(ct);
                        });
                        registered = true;

                        try
                        {
                            await WriteAsync(connection, request with { Nonce = connection.Nonce }, ct)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            // Remove exactly this generation's entry without touching a later reuse
                            // of the same request id.
                            TryTakePending(request.Id, entry, out _);
                            throw;
                        }
                    }
                    finally
                    {
                        connection.LifecycleGate.Release();
                    }

                    try
                    {
                        return await completion.Task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Forward cancellation to the connection that owned the request, never to a
                        // replacement generation.
                        await CancelAsync(request.Id, connection, CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }
                }
                finally
                {
                    if (registered)
                        registration.Dispose();
                }
            }
        }
        finally
        {
            if (slotReserved)
                _pendingSlots.Release();
        }
    }

    public async Task CancelAsync(string requestId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(requestId))
            return;

        var connection = _pending.TryGetValue(requestId, out var entry)
            ? entry.Owner
            : Volatile.Read(ref _connection);
        if (connection is null || connection.Faulted)
            return;

        await CancelAsync(requestId, connection, ct).ConfigureAwait(false);
    }

    private async Task CancelAsync(string requestId, Connection connection, CancellationToken ct)
    {
        await connection.LifecycleGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (connection.Faulted)
                return;

            await WriteAsync(connection, new BrowserPasskeyCancel(requestId, Nonce: connection.Nonce), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: the pending slot is already gone, so a failed cancel write changes nothing.
        }
        finally
        {
            connection.LifecycleGate.Release();
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

            // The connect factory classifies the exact named-pipe connect timeout itself. All other
            // failures propagate unchanged, including a TimeoutException produced after connection.
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

                if (response.Id is not null && TryTakePending(response.Id, connection, out var pending))
                    pending.Completion.TrySetResult(response with { Nonce = null });
            }
        }
        finally
        {
            // Publish the terminal generation state before waiting for an in-progress full-frame
            // write. New senders will reconnect instead of attaching to this completed read loop.
            connection.Faulted = true;
            if (_testHooks?.ConnectionFaultedBeforePendingFailureAsync is { } teardownBarrier)
                await teardownBarrier().ConfigureAwait(false);

            await connection.LifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                FailPendingOwnedBy(connection, failure);
            }
            finally
            {
                connection.LifecycleGate.Release();
            }
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

    private void FailPendingOwnedBy(Connection owner, Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (ReferenceEquals(pair.Value.Owner, owner)
                && TryTakePending(pair.Key, pair.Value, out var pending))
            {
                pending.Completion.TrySetException(exception);
            }
        }
    }

    private bool TryTakePending(
        string id,
        Connection owner,
        out PendingEntry entry)
    {
        if (!_pending.TryGetValue(id, out var candidate) || !ReferenceEquals(candidate.Owner, owner))
        {
            entry = null!;
            return false;
        }

        return TryTakePending(id, candidate, out entry);
    }

    private bool TryTakePending(string id, PendingEntry expected, out PendingEntry entry)
    {
        var pair = new KeyValuePair<string, PendingEntry>(id, expected);
        if (((ICollection<KeyValuePair<string, PendingEntry>>)_pending).Remove(pair))
        {
            _pendingSlots.Release();
            entry = expected;
            return true;
        }

        entry = null!;
        return false;
    }

    private void FailAllPending(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (TryTakePending(pair.Key, pair.Value, out var pending))
                pending.Completion.TrySetException(exception);
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
        _pendingSlots.Dispose();
    }

    private sealed class Connection : IAsyncDisposable
    {
        private readonly AppBridgeConnection _transport;

        public Connection(AppBridgeConnection transport) => _transport = transport;

        public Stream Stream => _transport.Stream;
        public string Nonce => _transport.Nonce;
        public CancellationTokenSource ReadCts { get; } = new();
        public SemaphoreSlim LifecycleGate { get; } = new(1, 1);
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

    private sealed record PendingEntry(
        Connection Owner,
        TaskCompletionSource<BrowserPasskeyResponse> Completion);
}

internal sealed class AppBridgeSessionTestHooks
{
    public Action<string>? PendingSlotReserved { get; init; }
    public Func<Task>? ConnectionFaultedBeforePendingFailureAsync { get; init; }
}
