using System.Collections.Concurrent;
using System.Text.Json;
using Core.Passkeys;

namespace BrowserNativeHost;

/// <summary>
/// Drives one Chrome native-messaging connection. A single read loop parses length-prefixed frames
/// and dispatches them without ever awaiting business work, so a slow ceremony can never stall the
/// next request or a cancel. Each active request id owns an independent cancellation source linked to
/// the session token and its own deadline; all responses funnel through one write lock so frames never
/// interleave. Cancellation is idempotent and never emits a response after the request was cancelled,
/// a fired deadline emits <see cref="PasskeyErrorCodes.DeadlineExceeded"/>, duplicate ids are rejected
/// with <see cref="PasskeyErrorCodes.RequestInProgress"/>, and a disconnect (EOF) or
/// <see cref="StopAsync"/> cancels every active request and drains its worker before returning.
/// Transport framing and the 1 MiB cap stay in <see cref="NativeMessageProtocol"/>. Dispatch of each
/// already-framed message is itself defended per-message: any exception from a single request's own
/// parameters/format (other than <see cref="OutOfMemoryException"/>) fails only that request with a
/// stable <c>invalid_request</c> error and the read loop keeps serving subsequent messages — it must
/// never fault the whole persistent session (see <see cref="ComputeDeadlineDelay"/>, which is
/// deliberately integer-only for the same reason: an out-of-range deadline must clamp, not throw).
/// </summary>
public sealed class NativeMessagingSession : IAsyncDisposable
{
    // Fallback deadline when the extension omits one, and the hard cap that no request may exceed.
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaximumDeadline = TimeSpan.FromSeconds(120);

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly IAppPasskeyBridgeClient _appBridge;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, ActiveRequest> _active = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly HashSet<Task> _workers = [];
    private readonly object _workersGate = new();
    private readonly CancellationTokenSource _sessionCts = new();
    private Task? _runTask;
    private int _disposed;

    public NativeMessagingSession(
        Stream input,
        Stream output,
        IAppPasskeyBridgeClient appBridge,
        TimeProvider? timeProvider = null)
    {
        _input = input;
        _output = output;
        _appBridge = appBridge;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task RunAsync(CancellationToken ct = default)
    {
        var task = RunCoreAsync(ct);
        _runTask = task;
        return task;
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
        var token = linked.Token;
        try
        {
            while (true)
            {
                NativeRequest? message;
                try
                {
                    message = await NativeMessageProtocol
                        .ReadAsync(_input, NativeMessageJsonContext.Default.NativeRequest, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (message is null)
                    break;

                try
                {
                    await DispatchAsync(message, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Session stopping mid-dispatch: fall through to the outer drain, same as a
                    // cancelled read.
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // One request's own parameters/format must never fault the whole persistent
                    // session (I-1): report a stable error for just this message and keep reading.
                    await TryReportDispatchFailureAsync(message.Id).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Disconnect (input EOF) / stop: mark the session cancelled FIRST so no worker emits a
            // late deadline/response frame after the peer is gone (a bare input EOF does not cancel
            // the session token on its own), then cancel every in-flight request and drain its worker.
            try { _sessionCts.Cancel(); }
            catch (ObjectDisposedException) { }

            foreach (var active in _active.Values)
                active.CancelRequest();

            Task[] workers;
            lock (_workersGate)
                workers = [.. _workers];
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
    }

    // Read-loop dispatch: fast, never awaits a business task except a cancel (which only cancels the
    // target's token and best-effort forwards upstream — both cheap).
    private async Task DispatchAsync(NativeRequest message, CancellationToken sessionToken)
    {
        switch (message.Type)
        {
            case "cancel":
                await CancelActiveAsync(message.Id).ConfigureAwait(false);
                return;
            case "ping":
                await WriteResponseAsync(new NativeResponse(
                    message.Id,
                    "pong",
                    true,
                    JsonSerializer.SerializeToElement(
                        new HostInfo(NativeMessagingHost.HostName, NativeMessagingHost.HostVersion),
                        NativeMessageJsonContext.Default.HostInfo))).ConfigureAwait(false);
                return;
            case "passkey.create":
            case "passkey.get":
                await AcceptRequestAsync(message, sessionToken).ConfigureAwait(false);
                return;
            case null or "":
                await WriteResponseAsync(Error(message.Id, "missing_type", "Native message type is required."))
                    .ConfigureAwait(false);
                return;
            default:
                await WriteResponseAsync(Error(
                    message.Id, "unknown_type", $"Unsupported native message type: {message.Type}"))
                    .ConfigureAwait(false);
                return;
        }
    }

    private async Task AcceptRequestAsync(NativeRequest message, CancellationToken sessionToken)
    {
        if (!IsValidRequestId(message.Id))
        {
            await WriteResponseAsync(Error(
                message.Id, "invalid_request", "Passkey request id must be 32 lowercase hex characters."))
                .ConfigureAwait(false);
            return;
        }

        var bridgeRequest = new BrowserPasskeyRequest(
            message.Id,
            message.Type,
            message.Payload,
            DeadlineUnixMs: message.DeadlineUnixMs,
            Context: message.Context);
        if (!PasskeyRequestParser.TryParse(bridgeRequest, out _, out var parseError))
        {
            await WriteResponseAsync(new NativeResponse(
                message.Id, "error", false, Error: ToNativeError(parseError))).ConfigureAwait(false);
            return;
        }

        // Deadline already past: fail immediately without touching the App bridge.
        var delay = ComputeDeadlineDelay(message.DeadlineUnixMs);
        if (delay <= TimeSpan.Zero)
        {
            await WriteResponseAsync(Error(
                message.Id, PasskeyErrorCodes.DeadlineExceeded, "The passkey request deadline has already passed."))
                .ConfigureAwait(false);
            return;
        }

        var deadlineCts = new CancellationTokenSource(delay, _timeProvider);
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, deadlineCts.Token);
        var active = new ActiveRequest(deadlineCts, requestCts);
        if (!_active.TryAdd(message.Id!, active))
        {
            active.Dispose();
            await WriteResponseAsync(Error(
                message.Id, PasskeyErrorCodes.RequestInProgress, "A passkey request with this id is already in progress."))
                .ConfigureAwait(false);
            return;
        }

        StartWorker(bridgeRequest, active, sessionToken);
    }

    private void StartWorker(BrowserPasskeyRequest request, ActiveRequest active, CancellationToken sessionToken)
    {
        var worker = RunWorkerAsync(request, active, sessionToken);
        lock (_workersGate)
            _workers.Add(worker);
        _ = worker.ContinueWith(
            completed =>
            {
                lock (_workersGate)
                    _workers.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunWorkerAsync(
        BrowserPasskeyRequest request,
        ActiveRequest active,
        CancellationToken sessionToken)
    {
        var token = active.RequestToken;
        try
        {
            BrowserPasskeyResponse response;
            try
            {
                // WaitAsync unblocks on cancellation even if the bridge does not honour the token itself.
                response = await _appBridge.SendAsync(request, token).WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A session/disconnect stop must never emit a late frame; a fired deadline reports
                // deadline_exceeded; an explicit cancel has already claimed the slot so neither fires.
                if (!sessionToken.IsCancellationRequested && active.TryClaimDeadline())
                    await WriteResponseAsync(Error(
                        request.Id, PasskeyErrorCodes.DeadlineExceeded, "The passkey request timed out."))
                        .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (
                ex is IOException or TimeoutException or InvalidDataException or UnauthorizedAccessException)
            {
                if (active.TryClaimResponded())
                    await WriteResponseAsync(Error(
                        request.Id,
                        "native_host_unavailable",
                        "WinVaultWarden is not running or its passkey bridge is unavailable."))
                        .ConfigureAwait(false);
                return;
            }
            catch (Exception) when (!sessionToken.IsCancellationRequested)
            {
                // Any other App-bridge failure that is NOT a transport/disconnect or a session stop:
                // a stable, code-only internal_error. No exception text/stack/path/token reaches the
                // browser frame (Task 7 sanitization). Under a stop the exception falls through to the
                // outer best-effort catch so no late frame is emitted.
                if (active.TryClaimResponded())
                    await WriteResponseAsync(Error(
                        request.Id, PasskeyErrorCodes.InternalError, "The passkey request failed."))
                        .ConfigureAwait(false);
                return;
            }

            // Only the first terminal outcome writes: a concurrent cancel suppresses the response.
            if (active.TryClaimResponded())
                await WriteResponseAsync(MapResponse(response, request.Id)).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort worker: a write failure must not fault the drain in RunCoreAsync's finally.
        }
        finally
        {
            _active.TryRemove(request.Id!, out _);
            active.Dispose();
        }
    }

    private async Task TryReportDispatchFailureAsync(string? id)
    {
        try
        {
            await WriteResponseAsync(Error(
                id, "invalid_request", "The passkey request could not be processed."))
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: a failure while reporting the failure must not fault the read loop either.
        }
    }

    private async Task CancelActiveAsync(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_active.TryGetValue(id, out var active))
            return;

        // Claim the slot exactly once so a repeated cancel is a no-op and never double-forwards upstream.
        if (!active.TryClaimCancelled())
            return;

        active.CancelRequest();
        try
        {
            await _appBridge.CancelAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort upstream cancel; the local request is already suppressed.
        }
    }

    private async Task WriteResponseAsync(NativeResponse response)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await NativeMessageProtocol
                .WriteAsync(_output, response, NativeMessageJsonContext.Default.NativeResponse, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Peer disconnected mid-write; nothing more can be delivered on this connection.
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // I-1: deliberately integer-only. deadlineUnixMs is attacker/extension-controlled and the wire
    // schema only enforces a lower bound, so it can legally arrive as long.MinValue/MaxValue, one ms
    // past DateTimeOffset's representable range, or a plausible unit bug (µs/ns instead of ms).
    // Never feed it to DateTimeOffset.FromUnixTimeMilliseconds — that throws ArgumentOutOfRangeException
    // for anything outside roughly [year 1, year 9999], which used to escape this single request and
    // take down the whole persistent session. Comparing plain longs against nowMs/capMs can't throw.
    private TimeSpan ComputeDeadlineDelay(long? deadlineUnixMs)
    {
        if (deadlineUnixMs is not { } ms)
            return DefaultDeadline;

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var capMs = nowMs + (long)MaximumDeadline.TotalMilliseconds;

        if (ms <= nowMs)
            return TimeSpan.Zero; // Already past (or right now): fail immediately, never touch the bridge.
        if (ms >= capMs)
            return MaximumDeadline; // Beyond the hard cap (including any overflow-scale value): clamp.

        return TimeSpan.FromMilliseconds(ms - nowMs);
    }

    // Stop accepting input, cancel every active request, and wait for all workers to drain.
    public async Task StopAsync()
    {
        _sessionCts.Cancel();
        var run = _runTask;
        if (run is null)
            return;

        try
        {
            await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (
            ex is EndOfStreamException or IOException or InvalidDataException or JsonException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await StopAsync().ConfigureAwait(false);
        _sessionCts.Dispose();
        _writeGate.Dispose();
    }

    private static NativeResponse MapResponse(BrowserPasskeyResponse response, string? requestId) =>
        new(response.Id ?? requestId, response.Type, response.Ok, response.Payload, ToNativeError(response.Error));

    private static NativeError? ToNativeError(BrowserPasskeyError? error) =>
        error is null ? null : new NativeError(error.Code);

    // Code-only wire: the message argument documents intent at the call site but never crosses the
    // wire (Task 7) — no exception text/stack/path may reach the browser.
    private static NativeResponse Error(string? id, string code, string message) =>
        new(id, "error", false, Error: new NativeError(code));

    // Wire-contract request id: exactly 32 lowercase hex characters (16 bytes).
    private static bool IsValidRequestId(string? id)
    {
        if (id is null || id.Length != 32)
            return false;
        foreach (var c in id)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }
        return true;
    }

    // One in-flight request: its deadline timer, a linked cancellation source, and a single-winner
    // terminal state so a response, a deadline, and a cancel can never each write a frame.
    private sealed class ActiveRequest : IDisposable
    {
        private const int Pending = 0;
        private const int Responded = 1;
        private const int Deadline = 2;
        private const int Cancelled = 3;

        private readonly CancellationTokenSource _deadlineCts;
        private readonly CancellationTokenSource _requestCts;
        private int _state;

        public ActiveRequest(CancellationTokenSource deadlineCts, CancellationTokenSource requestCts)
        {
            _deadlineCts = deadlineCts;
            _requestCts = requestCts;
        }

        public CancellationToken RequestToken => _requestCts.Token;

        public bool TryClaimResponded() => Interlocked.CompareExchange(ref _state, Responded, Pending) == Pending;

        public bool TryClaimDeadline() => Interlocked.CompareExchange(ref _state, Deadline, Pending) == Pending;

        public bool TryClaimCancelled() => Interlocked.CompareExchange(ref _state, Cancelled, Pending) == Pending;

        public void CancelRequest()
        {
            try
            {
                _requestCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            _requestCts.Dispose();
            _deadlineCts.Dispose();
        }
    }
}
