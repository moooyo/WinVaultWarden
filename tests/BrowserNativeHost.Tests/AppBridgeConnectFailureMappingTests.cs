using System.Buffers.Binary;
using System.Text.Json;
using Core.Passkeys;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BrowserNativeHost.Tests;

// Browser-task-7 remediation (review L1): an initial connect failure — the App simply isn't running
// or listening yet, the typical NamedPipe connect TimeoutException while the vault is locked — happens
// BEFORE the request is ever handed off to a connected App. That must map to client_not_handled, the
// extension's SOLE fallback trigger (protocol/passkey-errors.json: only client_not_handled has
// fallback:true). Once a connection HAS been established and a request goes out over it, a later
// disconnect/IOException must map to operation_aborted and never fall back — the App may already be
// mid-ceremony and a platform-authenticator retry behind its back would be unsafe. The two cases are
// told apart by an explicit marker created at the production named-pipe ConnectAsync timeout catch.
// The generic AppBridgeSession never infers listener absence from TimeoutException, so authorization,
// handshake, framing, and established-connection failures always abort without fallback.
public class AppBridgeConnectFailureMappingTests
{
    private const string Id81 = "00000000000000000000000000000081";
    private const string Id82 = "00000000000000000000000000000082";
    private const string Id83 = "00000000000000000000000000000083";
    private const string Id84 = "00000000000000000000000000000084";

    [Fact]
    public async Task PersistentSession_InitialConnectFailure_NeverReachedApp_MapsToClientNotHandled()
    {
        var bridge = new ThrowingBridge(new AppBridgeConnectFailedException(
            new TimeoutException("Timed out connecting to the WinVaultWarden passkey bridge.")));

        var response = await RunSingleRequestAsync(bridge, Id81);

        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.ClientNotHandled, response.Error?.Code);
    }

    [Fact]
    public async Task PersistentSession_EstablishedConnectionLost_MapsToOperationAborted_NeverClientNotHandled()
    {
        // Exactly what AppBridgeSession.ReadLoopAsync surfaces via FailAllPending once a connection
        // that WAS established for a prior request is lost mid-flight: a plain IOException, never
        // wrapped, because the App may already be running a ceremony for this request.
        var bridge = new ThrowingBridge(new IOException("App bridge disconnected."));

        var response = await RunSingleRequestAsync(bridge, Id82);

        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.OperationAborted, response.Error?.Code);
        Assert.NotEqual(PasskeyErrorCodes.ClientNotHandled, response.Error?.Code);
    }

    [Fact]
    public async Task PersistentSession_NonceMismatchAfterEstablishedConnection_AbortsWithoutFallback()
    {
        // A response nonce mismatch on an established connection (possible spoofing) must never be
        // treated as "App not handling this": operation_aborted, not client_not_handled.
        var bridge = new ThrowingBridge(new UnauthorizedAccessException("Passkey bridge response nonce mismatch."));

        var response = await RunSingleRequestAsync(bridge, Id83);

        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.OperationAborted, response.Error?.Code);
    }

    [Fact]
    public async Task LegacyHandleAsync_InitialConnectFailure_MapsToClientNotHandled()
    {
        var bridge = new ThrowingBridge(new AppBridgeConnectFailedException(
            new TimeoutException("Timed out connecting to the WinVaultWarden passkey bridge.")));

        var response = await NativeMessagingHost.HandleAsync(
            NativeMessagingSessionTests.Request(Id84, TimeSpan.FromSeconds(60)),
            bridge,
            TestContext.Current.CancellationToken);

        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.ClientNotHandled, response.Error?.Code);
    }

    [Fact]
    public async Task LegacyHandleAsync_EstablishedConnectionLost_MapsToOperationAborted()
    {
        var bridge = new ThrowingBridge(new IOException("App bridge disconnected."));

        var response = await NativeMessagingHost.HandleAsync(
            NativeMessagingSessionTests.Request(Id84, TimeSpan.FromSeconds(60)),
            bridge,
            TestContext.Current.CancellationToken);

        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.OperationAborted, response.Error?.Code);
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("invalid_data")]
    [InlineData("io")]
    [InlineData("timeout")]
    public async Task ConnectPhaseIntegrityFailure_AbortsInPersistentAndLegacyPaths(string failureKind)
    {
        await using var persistentBridge = new AppBridgeSession(
            _ => Task.FromException<AppBridgeConnection>(CreateIntegrityFailure(failureKind)));
        var persistent = await RunSingleRequestAsync(persistentBridge, Id83);

        Assert.False(persistent.Ok);
        Assert.Equal(PasskeyErrorCodes.OperationAborted, persistent.Error?.Code);
        Assert.NotEqual(PasskeyErrorCodes.ClientNotHandled, persistent.Error?.Code);

        await using var legacyBridge = new AppBridgeSession(
            _ => Task.FromException<AppBridgeConnection>(CreateIntegrityFailure(failureKind)));
        var legacy = await NativeMessagingHost.HandleAsync(
            NativeMessagingSessionTests.Request(Id84, TimeSpan.FromSeconds(60)),
            legacyBridge,
            TestContext.Current.CancellationToken);

        Assert.False(legacy.Ok);
        Assert.Equal(PasskeyErrorCodes.OperationAborted, legacy.Error?.Code);
        Assert.NotEqual(PasskeyErrorCodes.ClientNotHandled, legacy.Error?.Code);
    }

    private static Exception CreateIntegrityFailure(string kind) => kind switch
    {
        "unauthorized" => new UnauthorizedAccessException("Peer identity rejected."),
        "invalid_data" => new InvalidDataException("Handshake frame was malformed."),
        "io" => new IOException("Handshake transport failed."),
        "timeout" => new TimeoutException("A post-connect phase timed out."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static async Task<NativeResponse> RunSingleRequestAsync(IAppPasskeyBridgeClient bridge, string id)
    {
        var sink = new SingleResponseSink();
        await using var session = new NativeMessagingSession(
            new SingleFrameInput(Serialize(NativeMessagingSessionTests.Request(id, TimeSpan.FromSeconds(60)))),
            sink,
            bridge,
            new FakeTimeProvider(NativeMessagingSessionTests.Epoch));
        _ = session.RunAsync(TestContext.Current.CancellationToken);

        return await sink.Response.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    private static byte[] Serialize(NativeRequest request)
    {
        using var buffer = new MemoryStream();
        NativeMessageProtocol
            .WriteAsync(buffer, request, NativeMessageJsonContext.Default.NativeRequest)
            .GetAwaiter()
            .GetResult();
        return buffer.ToArray();
    }

    private sealed class ThrowingBridge : IAppPasskeyBridgeClient
    {
        private readonly Exception _exception;
        public ThrowingBridge(Exception exception) => _exception = exception;

        public Task<BrowserPasskeyResponse> SendAsync(BrowserPasskeyRequest request, CancellationToken ct = default) =>
            Task.FromException<BrowserPasskeyResponse>(_exception);

        public Task CancelAsync(string requestId, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Emits one frame, then blocks on reads (EOF never arrives on its own) until cancelled/disposed.
    private sealed class SingleFrameInput : Stream
    {
        private readonly byte[] _frame;
        private int _offset;
        public SingleFrameInput(byte[] frame) => _frame = frame;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_offset < _frame.Length)
            {
                var n = Math.Min(buffer.Length, _frame.Length - _offset);
                _frame.AsSpan(_offset, n).CopyTo(buffer.Span);
                _offset += n;
                return n;
            }
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0;
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

    // Reassembles length-prefixed NativeResponse frames and completes Response on the first one.
    private sealed class SingleResponseSink : Stream
    {
        private readonly object _lock = new();
        private readonly List<byte> _pending = [];
        public TaskCompletionSource<NativeResponse> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Append(ReadOnlySpan<byte> data)
        {
            lock (_lock)
            {
                _pending.AddRange(data);
                while (_pending.Count >= 4)
                {
                    var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending)[..4]);
                    if (_pending.Count < 4 + length)
                        break;
                    var payload = _pending.GetRange(4, length).ToArray();
                    _pending.RemoveRange(0, 4 + length);
                    var response = JsonSerializer.Deserialize(
                        payload, NativeMessageJsonContext.Default.NativeResponse);
                    if (response is not null)
                        Response.TrySetResult(response);
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
}
