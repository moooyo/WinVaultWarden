using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Core.BrowserIntegration;
using Core.Passkeys;

namespace App.Services;

public sealed class PasskeyBridgeServer : IAsyncDisposable
{
    public const int MaxActiveRequestsPerConnection = 8;
    public const int MaxConcurrentConnections = 4;

    private readonly Func<BrowserPasskeyRequest, CancellationToken, Task<BrowserPasskeyResponse>> _handleRequestAsync;
    private readonly IPipePeerAuthorizer _peerAuthorizer;
    private readonly Func<string, CancellationToken, Task> _listenAsync;
    private readonly SemaphoreSlim _connectionSlots = new(MaxConcurrentConnections, MaxConcurrentConnections);
    private readonly object _gate = new();
    private CancellationTokenSource? _shutdown;
    private Task? _listenTask;
    private Task _stopTask = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _disposed;

    public PasskeyBridgeServer(BrowserPasskeyRequestHandler handler, IPipePeerAuthorizer peerAuthorizer)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handleRequestAsync = handler.HandleAsync;
        _peerAuthorizer = peerAuthorizer ?? throw new ArgumentNullException(nameof(peerAuthorizer));
        _listenAsync = ListenAsync;
    }

    // Keeps listener lifecycle and connection dispatch independently testable without weakening the
    // production constructor or the authenticated named-pipe boundary.
    internal PasskeyBridgeServer(
        Func<BrowserPasskeyRequest, CancellationToken, Task<BrowserPasskeyResponse>> handleRequestAsync,
        IPipePeerAuthorizer peerAuthorizer,
        Func<string, CancellationToken, Task>? listenAsync = null)
    {
        _handleRequestAsync = handleRequestAsync ?? throw new ArgumentNullException(nameof(handleRequestAsync));
        _peerAuthorizer = peerAuthorizer ?? throw new ArgumentNullException(nameof(peerAuthorizer));
        _listenAsync = listenAsync ?? ListenAsync;
    }

    public bool IsRunning
    {
        get { lock (_gate) return _listenTask is { IsCompleted: false }; }
    }

    // Idempotent and restartable. A completed or faulted listener is reclaimed so a later Start can
    // recover with an independent cancellation source and a freshly rotated session nonce.
    public void Start()
    {
        CancellationTokenSource? completedShutdown = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_listenTask is { IsCompleted: false })
                return;

            if (_listenTask is { } completed)
            {
                if (completed.IsFaulted)
                    _ = completed.Exception;
                completedShutdown = _shutdown;
            }

            var sessionNonce = PasskeyBridgeHandshake.CreateServerNonce();
            _shutdown = new CancellationTokenSource();
            var token = _shutdown.Token;
            _listenTask = Task.Run(() => _listenAsync(sessionNonce, token));
        }

        completedShutdown?.Dispose();
    }

    // Stop listening, cancel every connection, and drain all request handlers before returning.
    // Fields are cleared under the lifecycle lock so StopAsync remains idempotent and restartable.
    public async Task StopAsync()
    {
        Task stopTask;
        lock (_gate)
        {
            if (_listenTask is null)
            {
                stopTask = _stopTask;
            }
            else
            {
                var listen = _listenTask;
                var cts = _shutdown!;
                _listenTask = null;
                _shutdown = null;

                // Retain every in-progress drain. This lets a concurrent StopAsync or DisposeAsync
                // wait for an earlier listener generation instead of returning while its connections
                // can still release slots.
                var priorStop = _stopTask.IsCompletedSuccessfully ? Task.CompletedTask : _stopTask;
                _stopTask = Task.WhenAll(priorStop, DrainListenerAsync(listen, cts));
                stopTask = _stopTask;
            }
        }

        await stopTask.ConfigureAwait(false);
    }

    private static async Task DrainListenerAsync(Task listen, CancellationTokenSource cts)
    {
        try
        {
            try
            {
                cts.Cancel();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Cancellation callback failures must not prevent the listener itself from draining.
            }

            try
            {
                await listen.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Await observes the historical listener fault. Cleanup converges without surfacing
                // it a second time; an out-of-memory failure remains fatal and propagates.
            }
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task ListenAsync(string sessionNonce, CancellationToken ct)
    {
        // Track every accepted connection and join them on shutdown. Each connection in turn drains
        // its own request handlers before its task completes.
        var connections = new HashSet<Task>();
        var connectionsGate = new object();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pipe = CreateSecuredPipe();

                try
                {
                    await pipe.WaitForConnectionAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync();
                    break;
                }
                catch (IOException)
                {
                    await pipe.DisposeAsync();
                    continue;
                }

                // No authenticated response can be sent before the handshake, so an excess connection
                // is closed immediately. The slot wrapper releases on every exit path, including
                // handshake rejection, peer disconnect, listener cancellation, and handler failure.
                if (!_connectionSlots.Wait(0))
                {
                    await pipe.DisposeAsync();
                    continue;
                }

                var connection = HandleConnectionWithSlotAsync(pipe, sessionNonce, ct);
                lock (connectionsGate)
                    connections.Add(connection);
                _ = connection.ContinueWith(
                    completed =>
                    {
                        lock (connectionsGate)
                            connections.Remove(completed);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            Task[] pending;
            lock (connectionsGate)
                pending = [.. connections];
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionWithSlotAsync(
        NamedPipeServerStream pipe,
        string sessionNonce,
        CancellationToken ct)
    {
        try
        {
            await HandleConnectionAsync(pipe, sessionNonce, ct).ConfigureAwait(false);
        }
        finally
        {
            _connectionSlots.Release();
        }
    }

    private static NamedPipeServerStream CreateSecuredPipe()
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve the current Windows user SID.");

        var security = new PipeSecurity();
        // Grant the interactive owner full duplex access to the bridge.
        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        // Explicitly deny anything arriving over the network (defence in depth;
        // named pipes are local but a remote SID must never reach this bridge).
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));

        return NamedPipeServerStreamAcl.Create(
            BrowserPasskeyBridge.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, string sessionNonce, CancellationToken ct)
    {
        // This task is tracked but not awaited inline by ListenAsync. It must never throw: every
        // pre-handshake failure closes silently, while an authenticated peer proceeds to its session.
        await using (pipe)
        {
            PasskeyBridgeSession session;
            try
            {
                // CompleteServerAsync authorizes the kernel-reported peer before writing or reading
                // any hello bytes.
                session = await PasskeyBridgeHandshake.CompleteServerAsync(
                    pipe,
                    sessionNonce,
                    (uint)Environment.ProcessId,
                    innerCt => _peerAuthorizer.AuthorizeClientAsync(
                        pipe, BrowserNativeHostLocator.ResolveCurrent(), innerCt),
                    ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch
            {
                return;
            }

            await RunConnectionAsync(pipe, session.Nonce, ct);
        }
    }

    // One authenticated connection has a non-blocking read loop, per-request cancellation, and a
    // single response writer. The Stream signature permits transport-level tests without bypassing
    // the production handshake, which remains exclusively in HandleConnectionAsync.
    internal async Task RunConnectionAsync(Stream pipe, string nonce, CancellationToken ct)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = connectionCts.Token;
        var active = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var activeSlots = new SemaphoreSlim(MaxActiveRequestsPerConnection, MaxActiveRequestsPerConnection);
        var writeGate = new SemaphoreSlim(1, 1);
        var handlers = new HashSet<Task>();
        var handlersGate = new object();

        try
        {
            while (!token.IsCancellationRequested)
            {
                BrowserPasskeyRequest? envelope;
                try
                {
                    envelope = await BrowserPasskeyMessageProtocol.ReadAsync(
                        pipe, PasskeyJsonContext.Default.BrowserPasskeyRequest, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (
                    ex is IOException or InvalidDataException or EndOfStreamException or JsonException)
                {
                    break; // A disconnect or malformed frame terminates only this connection.
                }

                if (envelope is null)
                    break;

                // Every envelope is nonce-bound. A mismatch or replay closes the entire connection.
                if (!PasskeyBridgeHandshake.NonceMatches(envelope.Nonce, nonce))
                    break;

                if (envelope.Type == "cancel")
                {
                    if (envelope.Id is not null && active.TryGetValue(envelope.Id, out var activeCts))
                    {
                        try { activeCts.Cancel(); }
                        catch (ObjectDisposedException) { }
                    }
                    continue;
                }

                if (envelope.Id is null)
                    continue;

                if (active.ContainsKey(envelope.Id))
                {
                    await WriteResponseAsync(
                        pipe,
                        writeGate,
                        BrowserPasskeyResponse.Failure(envelope.Id, PasskeyErrorCodes.RequestInProgress) with { Nonce = nonce });
                    continue;
                }

                if (!activeSlots.Wait(0))
                {
                    await WriteResponseAsync(
                        pipe,
                        writeGate,
                        BrowserPasskeyResponse.Failure(envelope.Id, PasskeyErrorCodes.RateLimited) with { Nonce = nonce });
                    continue;
                }

                var requestCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (!active.TryAdd(envelope.Id, requestCts))
                {
                    requestCts.Dispose();
                    activeSlots.Release();
                    await WriteResponseAsync(
                        pipe,
                        writeGate,
                        BrowserPasskeyResponse.Failure(envelope.Id, PasskeyErrorCodes.RequestInProgress) with { Nonce = nonce });
                    continue;
                }

                var handler = HandleRequestAsync(
                    pipe, writeGate, activeSlots, active, envelope, requestCts, nonce);
                lock (handlersGate)
                    handlers.Add(handler);
                _ = handler.ContinueWith(
                    completed =>
                    {
                        lock (handlersGate)
                            handlers.Remove(completed);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            // Disconnect and shutdown cancel every request, then drain handlers before releasing
            // connection-scoped synchronization primitives.
            foreach (var requestCts in active.Values)
            {
                try { requestCts.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            Task[] pending;
            lock (handlersGate)
                pending = [.. handlers];
            await Task.WhenAll(pending).ConfigureAwait(false);
            activeSlots.Dispose();
            writeGate.Dispose();
        }
    }

    private async Task HandleRequestAsync(
        Stream pipe,
        SemaphoreSlim writeGate,
        SemaphoreSlim activeSlots,
        ConcurrentDictionary<string, CancellationTokenSource> active,
        BrowserPasskeyRequest envelope,
        CancellationTokenSource requestCts,
        string nonce)
    {
        try
        {
            var response = await _handleRequestAsync(envelope, requestCts.Token).ConfigureAwait(false);
            // Explicit cancellation and connection shutdown suppress late responses.
            if (!requestCts.IsCancellationRequested)
                await WriteResponseAsync(pipe, writeGate, response with { Nonce = nonce }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is terminal and produces no response.
        }
        catch
        {
            // After authentication, report a best-effort nonce-bound code-only error.
            try
            {
                if (!requestCts.IsCancellationRequested)
                    await WriteResponseAsync(
                        pipe,
                        writeGate,
                        BrowserPasskeyResponse.Failure(envelope.Id!, PasskeyErrorCodes.InternalError) with { Nonce = nonce })
                        .ConfigureAwait(false);
            }
            catch
            {
                // The peer disconnected while the error was being written.
            }
        }
        finally
        {
            if (envelope.Id is not null && active.TryRemove(envelope.Id, out var removed))
            {
                removed.Dispose();
                activeSlots.Release();
            }
        }
    }

    // One connection serializes every response. Once the gate is held, CancellationToken.None keeps
    // a cancellation from tearing a length-prefixed frame in half.
    private static async Task WriteResponseAsync(
        Stream pipe,
        SemaphoreSlim writeGate,
        BrowserPasskeyResponse response)
    {
        await writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await BrowserPasskeyMessageProtocol.WriteAsync(
                pipe, response, PasskeyJsonContext.Default.BrowserPasskeyResponse, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The peer disconnected; this connection cannot deliver another frame.
        }
        finally
        {
            writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionSlots.Dispose();
        }
    }
}
