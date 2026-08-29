using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using Core.Passkeys;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BrowserNativeHost.Tests;

// Concurrency contract for the persistent native-messaging session: one read loop that never blocks
// on business work, per-id cancellation, deadline enforcement with a hard cap, duplicate-id
// rejection, disconnect fan-out cancellation, and a single write lock that keeps response frames
// intact. Determinism comes from HandoffStream, which only releases a SendAsync once the read loop
// has finished dispatching that frame and returned for the next one.
public class NativeMessagingSessionTests
{
    internal static readonly DateTimeOffset Epoch = new(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);

    private const string Id11 = "00000000000000000000000000000011";
    private const string Id12 = "00000000000000000000000000000012";
    private const string Id13 = "00000000000000000000000000000013";
    private const string Id31 = "00000000000000000000000000000031";
    private const string Id32 = "00000000000000000000000000000032";
    private const string Id33 = "00000000000000000000000000000033";
    private const string Id34 = "00000000000000000000000000000034";
    private const string Id36 = "00000000000000000000000000000036";
    private const string Id38 = "00000000000000000000000000000038";
    private const string Id41 = "00000000000000000000000000000041";
    private const string Id51 = "00000000000000000000000000000051";
    private const string Id52 = "00000000000000000000000000000052";
    private const string Id61 = "00000000000000000000000000000061";

    [Fact]
    public async Task ReadLoop_DoesNotWaitForFirstBusinessTask()
    {
        var first = new TaskCompletionSource<BrowserPasskeyResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new ControlledBridge(first);
        await using var fixture = await SessionFixture.StartAsync(bridge);

        await fixture.SendAsync(Request(Id11, TimeSpan.FromSeconds(60)));
        await fixture.SendAsync(Request(Id12, TimeSpan.FromSeconds(60)));

        await bridge.SecondRequestSeen.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(2, bridge.SendCalls);

        first.SetResult(Success(Id11));
    }

    [Fact]
    public async Task Cancel_IsIdempotentAndLateResultIsNotWritten()
    {
        var pending = new TaskCompletionSource<BrowserPasskeyResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await SessionFixture.StartAsync(new ControlledBridge(pending));
        await fixture.SendAsync(Request(Id13, TimeSpan.FromSeconds(60)));

        await fixture.SendCancelAsync(Id13);
        await fixture.SendCancelAsync(Id13);
        pending.SetResult(Success(Id13));

        Assert.Equal(1, fixture.BridgeCancelCalls);
        Assert.Empty(fixture.ResponsesFor(Id13));
    }

    [Fact]
    public async Task Disconnect_CancelsEveryActiveRequest()
    {
        await using var fixture = await SessionFixture.StartWithTwoPendingAsync();
        await fixture.DisconnectInputAsync();
        await fixture.AllBridgeTokensCancelled.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeadlineInThePast_FailsImmediatelyWithoutCallingBridge()
    {
        var bridge = new ControlledBridge();
        await using var fixture = await SessionFixture.StartAsync(bridge);

        await fixture.SendAsync(Request(Id31, TimeSpan.FromSeconds(-1)));

        var response = Assert.Single(fixture.ResponsesFor(Id31));
        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.DeadlineExceeded, response.Error?.Code);
        Assert.Equal(0, bridge.SendCalls);
    }

    [Fact]
    public async Task DeadlineBeyondHardCap_IsClampedToMaximum()
    {
        var bridge = new ControlledBridge();
        await using var fixture = await SessionFixture.StartAsync(bridge);

        // 200s exceeds the 120s hard cap; nothing may fire before the cap elapses.
        await fixture.SendAsync(Request(Id32, TimeSpan.FromSeconds(200)));
        Assert.Empty(fixture.ResponsesFor(Id32));

        fixture.Time.Advance(TimeSpan.FromSeconds(120));

        var responses = await fixture.WaitForResponsesAsync(
            r => r.Any(x => x.Id == Id32), TimeSpan.FromSeconds(2));
        var response = Assert.Single(responses, x => x.Id == Id32);
        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.DeadlineExceeded, response.Error?.Code);
    }

    [Fact]
    public async Task DeadlineExactlyAtHardCap_FiresOnlyAfterFullCapWindow()
    {
        var bridge = new ControlledBridge();
        await using var fixture = await SessionFixture.StartAsync(bridge);

        await fixture.SendAsync(Request(Id33, TimeSpan.FromSeconds(120)));
        Assert.Empty(fixture.ResponsesFor(Id33));

        fixture.Time.Advance(TimeSpan.FromSeconds(120));

        var responses = await fixture.WaitForResponsesAsync(
            r => r.Any(x => x.Id == Id33), TimeSpan.FromSeconds(2));
        var response = Assert.Single(responses, x => x.Id == Id33);
        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.DeadlineExceeded, response.Error?.Code);
    }

    [Fact]
    public async Task DeadlineJustOverHardCap_ClampsToCapNotToTheRequestedOffset()
    {
        var bridge = new ControlledBridge();
        await using var fixture = await SessionFixture.StartAsync(bridge);

        // 1 ms beyond the cap must still clamp to exactly 120s, not 120.001s.
        await fixture.SendAsync(Request(Id34, TimeSpan.FromSeconds(120) + TimeSpan.FromMilliseconds(1)));

        fixture.Time.Advance(TimeSpan.FromSeconds(120));

        var responses = await fixture.WaitForResponsesAsync(
            r => r.Any(x => x.Id == Id34), TimeSpan.FromSeconds(2));
        var response = Assert.Single(responses, x => x.Id == Id34);
        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.DeadlineExceeded, response.Error?.Code);
    }

    // I-1: an out-of-range deadlineUnixMs must never throw (it used to crash the whole persistent
    // session via DateTimeOffset.FromUnixTimeMilliseconds's ArgumentOutOfRangeException). A wildly
    // future value — long.MaxValue, one ms past DateTimeOffset.MaxValue's unix-ms range, or a
    // plausible microsecond-scale unit bug — must clamp to the 120s hard cap, and the session must
    // keep serving subsequent requests afterwards.
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(253402300800000L)]
    [InlineData(1_780_000_000_000_000L)]
    public async Task ExtremeFutureDeadline_ClampsToHardCapInsteadOfThrowing(long deadlineUnixMs)
    {
        var bridge = new ControlledBridge();
        await using var fixture = await SessionFixture.StartAsync(bridge);
        const string id = "00000000000000000000000000000035";

        await fixture.SendAsync(new NativeRequest(
            id, "passkey.get", GetPayload(), DeadlineUnixMs: deadlineUnixMs, Context: Context()))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(fixture.ResponsesFor(id));

        fixture.Time.Advance(TimeSpan.FromSeconds(120));

        var responses = await fixture.WaitForResponsesAsync(r => r.Any(x => x.Id == id), TimeSpan.FromSeconds(2));
        var response = Assert.Single(responses, x => x.Id == id);
        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.DeadlineExceeded, response.Error?.Code);

        // The session must not have faulted: a normal follow-up request is still processed. Its
        // deadline must be relative to the *current* fake time (already advanced by 120s above).
        await fixture.SendAsync(RequestAt(fixture.Time, Id36, TimeSpan.FromSeconds(60)))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await bridge.SecondRequestSeen.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    // I-1: long.MinValue and other far-past values must fail immediately (never touching the
    // bridge) instead of throwing, and must not fault the session either.
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(0L)]
    public async Task ExtremePastOrZeroDeadline_FailsImmediatelyInsteadOfThrowing(long deadlineUnixMs)
    {
        var bridge = new ControlledBridge();
        await using var fixture = await SessionFixture.StartAsync(bridge);
        const string id = "00000000000000000000000000000037";

        await fixture.SendAsync(new NativeRequest(
            id, "passkey.get", GetPayload(), DeadlineUnixMs: deadlineUnixMs, Context: Context()))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var response = Assert.Single(fixture.ResponsesFor(id));
        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.DeadlineExceeded, response.Error?.Code);
        Assert.Equal(0, bridge.SendCalls);

        // The session must not have faulted: a normal follow-up request is still processed end to end.
        await fixture.SendAsync(Request(Id38, TimeSpan.FromSeconds(60)))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        bridge.Complete(Id38, Success(Id38));
        var followUp = await fixture.WaitForResponsesAsync(r => r.Any(x => x.Id == Id38), TimeSpan.FromSeconds(2));
        Assert.True(Assert.Single(followUp, x => x.Id == Id38).Ok);
    }

    [Fact]
    public async Task DuplicateActiveId_IsRejectedWithRequestInProgress()
    {
        var bridge = new ControlledBridge();
        await using var fixture = await SessionFixture.StartAsync(bridge);

        await fixture.SendAsync(Request(Id41, TimeSpan.FromSeconds(60)));
        await fixture.SendAsync(Request(Id41, TimeSpan.FromSeconds(60)));

        var rejected = Assert.Single(fixture.ResponsesFor(Id41));
        Assert.False(rejected.Ok);
        Assert.Equal(PasskeyErrorCodes.RequestInProgress, rejected.Error?.Code);
        Assert.Equal(1, bridge.SendCalls);
    }

    [Fact]
    public async Task NinthActiveRequest_IsRateLimitedWithoutCallingBridge()
    {
        var bridge = new ControlledBridge();
        await using var fixture = await SessionFixture.StartAsync(bridge);
        var ids = Enumerable.Range(1, NativeMessagingSession.MaxActiveRequests + 1)
            .Select(value => value.ToString("x32"))
            .ToArray();

        foreach (var id in ids.Take(NativeMessagingSession.MaxActiveRequests))
            await fixture.SendAsync(Request(id, TimeSpan.FromSeconds(60)));

        await fixture.SendAsync(Request(ids[^1], TimeSpan.FromSeconds(60)));

        var rejected = Assert.Single(fixture.ResponsesFor(ids[^1]));
        Assert.False(rejected.Ok);
        Assert.Equal(PasskeyErrorCodes.RateLimited, rejected.Error?.Code);
        Assert.Equal(NativeMessagingSession.MaxActiveRequests, bridge.SendCalls);
    }

    [Fact]
    public async Task ConcurrentCompletions_WriteTwoIntactFrames()
    {
        var bridge = new ControlledBridge();
        await using var fixture = await SessionFixture.StartAsync(bridge);

        await fixture.SendAsync(Request(Id51, TimeSpan.FromSeconds(60)));
        await fixture.SendAsync(Request(Id52, TimeSpan.FromSeconds(60)));

        // Release both business tasks together; the shared write lock must still emit two whole frames.
        bridge.Complete(Id51, Success(Id51));
        bridge.Complete(Id52, Success(Id52));

        var responses = await fixture.WaitForResponsesAsync(
            r => r.Count(x => x.Id is Id51 or Id52) >= 2, TimeSpan.FromSeconds(2));
        Assert.Contains(responses, r => r.Id == Id51 && r.Ok);
        Assert.Contains(responses, r => r.Id == Id52 && r.Ok);
    }

    [Fact]
    public async Task StopAsync_WaitsForActiveRequestsToObserveCancellation()
    {
        var bridge = new ControlledBridge();
        await using var fixture = await SessionFixture.StartAsync(bridge);
        await fixture.SendAsync(Request(Id61, TimeSpan.FromSeconds(60)));

        await fixture.StopAsync();

        // Draining is part of StopAsync, so the in-flight token is already observed cancelled and no
        // late response leaked out.
        await fixture.AllBridgeTokensCancelled.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Empty(fixture.ResponsesFor(Id61));
    }

    [Fact]
    public async Task MalformedRequestId_IsRejectedWithoutCallingBridge()
    {
        var bridge = new ControlledBridge();
        await using var fixture = await SessionFixture.StartAsync(bridge);

        await fixture.SendAsync(new NativeRequest(
            "not-a-valid-id", "passkey.get", GetPayload(),
            DeadlineUnixMs: (Epoch + TimeSpan.FromSeconds(60)).ToUnixTimeMilliseconds(),
            Context: Context()));

        var response = Assert.Single(fixture.ResponsesFor("not-a-valid-id"));
        Assert.False(response.Ok);
        Assert.Equal("invalid_request", response.Error?.Code);
        Assert.Equal(0, bridge.SendCalls);
    }

    internal static NativeRequest Request(string id, TimeSpan deadlineOffset) =>
        new(id, "passkey.get", GetPayload(),
            DeadlineUnixMs: (Epoch + deadlineOffset).ToUnixTimeMilliseconds(),
            Context: Context());

    // Like Request, but relative to the fixture's *current* fake time rather than the fixed Epoch —
    // needed for a follow-up request sent after the test has already advanced the clock.
    internal static NativeRequest RequestAt(TimeProvider time, string id, TimeSpan deadlineOffset) =>
        new(id, "passkey.get", GetPayload(),
            DeadlineUnixMs: (time.GetUtcNow() + deadlineOffset).ToUnixTimeMilliseconds(),
            Context: Context());

    internal static BrowserPasskeyResponse Success(string id) =>
        new(id, "passkey.get", true, JsonSerializer.SerializeToElement(
            new PasskeyGetAssertionPayload("AQID", "auth", "client", "sig", "BQYH"),
            PasskeyJsonContext.Default.PasskeyGetAssertionPayload));

    internal static JsonElement GetPayload()
    {
        using var doc = JsonDocument.Parse(
            """{"origin":"https://example.com","challenge":"AQIDBA","allowCredentials":[]}""");
        return doc.RootElement.Clone();
    }

    internal static BrowserRequestContext Context() =>
        new("https://example.com", "https://example.com", 42, 7, "doc-1", true);

    // ── Deterministic in-memory transport ────────────────────────────────────────────────────

    // Input pipe whose WriteFrameAsync completes only when the read loop returns to read the *next*
    // frame — i.e. after it has fully dispatched (or fully cancelled) the previous message.
    internal sealed class HandoffStream : Stream
    {
        private readonly object _lock = new();
        private readonly Queue<Frame> _frames = new();
        private Frame? _current;
        private bool _completed;
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class Frame
        {
            public required byte[] Data { get; init; }
            public int Offset { get; set; }
            public TaskCompletionSource Consumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Remaining => Data.Length - Offset;
        }

        public Task WriteFrameAsync(byte[] data)
        {
            var frame = new Frame { Data = data };
            lock (_lock)
            {
                _frames.Enqueue(frame);
                Pulse();
            }
            return frame.Consumed.Task;
        }

        public void CompleteWriting()
        {
            lock (_lock)
            {
                _completed = true;
                Pulse();
            }
        }

        private void Pulse()
        {
            var signal = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.TrySetResult();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (true)
            {
                Task signal;
                lock (_lock)
                {
                    if (_current is { Remaining: 0 })
                    {
                        _current.Consumed.TrySetResult();
                        _current = null;
                    }

                    if (_current is null && _frames.Count > 0)
                        _current = _frames.Dequeue();

                    if (_current is not null)
                    {
                        var n = Math.Min(buffer.Length, _current.Remaining);
                        _current.Data.AsSpan(_current.Offset, n).CopyTo(buffer.Span);
                        _current.Offset += n;
                        return n;
                    }

                    if (_completed)
                        return 0;

                    signal = _signal.Task;
                }

                await signal.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Output pipe that reassembles length-prefixed NativeResponse frames as they are written. A
    // corrupt/interleaved frame would fail to parse, so successful parses prove the frames stayed whole.
    internal sealed class FrameSink : Stream
    {
        private readonly object _lock = new();
        private readonly List<byte> _pending = [];
        private readonly List<NativeResponse> _responses = [];
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<NativeResponse> Snapshot()
        {
            lock (_lock)
                return _responses.ToArray();
        }

        public async Task<IReadOnlyList<NativeResponse>> WaitForAsync(
            Func<IReadOnlyList<NativeResponse>, bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                Task signal;
                lock (_lock)
                {
                    var snapshot = _responses.ToArray();
                    if (predicate(snapshot))
                        return snapshot;
                    signal = _signal.Task;
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("Timed out waiting for native responses.");
                await signal.WaitAsync(remaining).ConfigureAwait(false);
            }
        }

        private void Append(ReadOnlySpan<byte> data)
        {
            lock (_lock)
            {
                _pending.AddRange(data);
                var added = false;
                while (_pending.Count >= 4)
                {
                    var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                        CollectionsMarshal.AsSpan(_pending)[..4]);
                    if (_pending.Count < 4 + length)
                        break;
                    var payload = _pending.GetRange(4, length).ToArray();
                    _pending.RemoveRange(0, 4 + length);
                    var response = JsonSerializer.Deserialize(
                        payload, NativeMessageJsonContext.Default.NativeResponse);
                    if (response is not null)
                    {
                        _responses.Add(response);
                        added = true;
                    }
                }

                if (added)
                {
                    var signal = _signal;
                    _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    signal.TrySetResult();
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Append(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => Append(buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Append(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Append(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    internal sealed class ControlledBridge : IAppPasskeyBridgeClient
    {
        private readonly TaskCompletionSource<BrowserPasskeyResponse>? _firstResult;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserPasskeyResponse>> _pending = new(StringComparer.Ordinal);
        private readonly object _tokensLock = new();
        private readonly List<CancellationToken> _sendTokens = [];
        private readonly TaskCompletionSource _allTokensCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sendCalls;
        private int _cancelCalls;

        public ControlledBridge(TaskCompletionSource<BrowserPasskeyResponse>? firstResult = null) =>
            _firstResult = firstResult;

        public TaskCompletionSource SecondRequestSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SendCalls => Volatile.Read(ref _sendCalls);
        public int CancelCalls => Volatile.Read(ref _cancelCalls);
        public Task AllBridgeTokensCancelled => _allTokensCancelled.Task;

        public Task<BrowserPasskeyResponse> SendAsync(BrowserPasskeyRequest request, CancellationToken ct = default)
        {
            var ordinal = Interlocked.Increment(ref _sendCalls);
            lock (_tokensLock)
                _sendTokens.Add(ct);
            ct.Register(OnTokenCancelled);
            if (ordinal >= 2)
                SecondRequestSeen.TrySetResult();

            if (ordinal == 1 && _firstResult is not null)
                return _firstResult.Task;

            var completion = new TaskCompletionSource<BrowserPasskeyResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[request.Id!] = completion;
            return completion.Task;
        }

        public Task CancelAsync(string requestId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _cancelCalls);
            return Task.CompletedTask;
        }

        public void Complete(string id, BrowserPasskeyResponse response)
        {
            if (_pending.TryGetValue(id, out var completion))
                completion.TrySetResult(response);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void OnTokenCancelled()
        {
            lock (_tokensLock)
            {
                if (_sendTokens.Count > 0 && _sendTokens.TrueForAll(t => t.IsCancellationRequested))
                    _allTokensCancelled.TrySetResult();
            }
        }
    }

    internal sealed class SessionFixture : IAsyncDisposable
    {
        private readonly HandoffStream _input = new();
        private readonly FrameSink _output = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly NativeMessagingSession _session;
        private readonly Task _run;

        private SessionFixture(ControlledBridge bridge, FakeTimeProvider time)
        {
            Bridge = bridge;
            Time = time;
            _session = new NativeMessagingSession(_input, _output, bridge, time);
            _run = _session.RunAsync(_cts.Token);
        }

        public FakeTimeProvider Time { get; }
        public ControlledBridge Bridge { get; }
        public int BridgeCancelCalls => Bridge.CancelCalls;
        public Task AllBridgeTokensCancelled => Bridge.AllBridgeTokensCancelled;

        public static Task<SessionFixture> StartAsync(ControlledBridge bridge) =>
            Task.FromResult(new SessionFixture(bridge, new FakeTimeProvider(Epoch)));

        public static async Task<SessionFixture> StartWithTwoPendingAsync()
        {
            var fixture = new SessionFixture(new ControlledBridge(), new FakeTimeProvider(Epoch));
            await fixture.SendAsync(Request("00000000000000000000000000000021", TimeSpan.FromSeconds(60)));
            await fixture.SendAsync(Request("00000000000000000000000000000022", TimeSpan.FromSeconds(60)));
            return fixture;
        }

        public Task SendAsync(NativeRequest request) => _input.WriteFrameAsync(Serialize(request));

        public Task SendCancelAsync(string id) =>
            _input.WriteFrameAsync(Serialize(new NativeRequest(id, "cancel")));

        public Task DisconnectInputAsync()
        {
            _input.CompleteWriting();
            return Task.CompletedTask;
        }

        public Task StopAsync() => _session.StopAsync();

        public IReadOnlyList<NativeResponse> ResponsesFor(string id) =>
            _output.Snapshot().Where(r => r.Id == id).ToArray();

        public Task<IReadOnlyList<NativeResponse>> WaitForResponsesAsync(
            Func<IReadOnlyList<NativeResponse>, bool> predicate, TimeSpan timeout) =>
            _output.WaitForAsync(predicate, timeout);

        private static byte[] Serialize(NativeRequest request)
        {
            using var buffer = new MemoryStream();
            NativeMessageProtocol
                .WriteAsync(buffer, request, NativeMessageJsonContext.Default.NativeRequest)
                .GetAwaiter()
                .GetResult();
            return buffer.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await _session.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
