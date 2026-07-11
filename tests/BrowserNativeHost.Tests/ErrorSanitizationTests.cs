using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Core.Passkeys;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BrowserNativeHost.Tests;

// Task 7 sanitization: no internal exception text may reach any observable surface. The fixed
// Sentinel deliberately embeds every shape we must never leak — a marker token, a Windows profile
// path, a credential, and a stack hint. Whatever the App bridge or the transport throws, the
// browser-visible native frame must be a code-only {"code":"internal_error"} (or a stable transport
// note on stderr), never the message, path, token or stack.
public class ErrorSanitizationTests
{
    private const string Sentinel =
        @"SENTINEL_INTERNAL C:\Users\alice\vault token=secret stack=BrowserPasskey";

    private static readonly string[] Forbidden =
        ["SENTINEL_INTERNAL", @"C:\Users", "token=secret", "stack="];

    private const string Id71 = "00000000000000000000000000000071";

    [Fact]
    public async Task ThrowingAppBridge_ProducesCodeOnlyInternalError_WithoutLeakingSentinel()
    {
        var sink = new RecordingSink();
        var bridge = new ThrowingBridge(new InvalidOperationException(Sentinel));
        await using var session = new NativeMessagingSession(
            new SingleFrameInput(Serialize(NativeMessagingSessionTests.Request(Id71, TimeSpan.FromSeconds(60)))),
            sink,
            bridge,
            new FakeTimeProvider(NativeMessagingSessionTests.Epoch));
        _ = session.RunAsync(TestContext.Current.CancellationToken);

        var responses = await sink.WaitForAsync(r => r.Any(x => x.Id == Id71), TimeSpan.FromSeconds(2));
        var response = Assert.Single(responses, x => x.Id == Id71);

        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.InternalError, response.Error?.Code);
        var responseJson = JsonSerializer.Serialize(response, NativeMessageJsonContext.Default.NativeResponse);
        AssertNoSentinel(sink.RawText());
        AssertNoSentinel(responseJson);
        // Page-visible native frame is code-only: no human-readable message field crosses the wire.
        Assert.DoesNotContain("message", responseJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransportTermination_WritesStableStderrNote_WithoutLeakingSentinel()
    {
        var error = new StringWriter();
        var exit = await NativeMessagingHost.RunAsync(
            new ThrowingInput(new IOException(Sentinel)),
            Stream.Null,
            error,
            CancellationToken.None,
            new ThrowingBridge(new InvalidOperationException(Sentinel)));

        Assert.Equal(1, exit);
        AssertNoSentinel(error.ToString());
    }

    private static void AssertNoSentinel(string text)
    {
        foreach (var token in Forbidden)
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
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

    private sealed class ThrowingInput : Stream
    {
        private readonly Exception _exception;
        public ThrowingInput(Exception exception) => _exception = exception;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromException<int>(_exception);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Task.FromException<int>(_exception);
        public override int Read(byte[] buffer, int offset, int count) => throw _exception;
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

    // Captures every raw byte written (for leak scanning) while reassembling NativeResponse frames.
    private sealed class RecordingSink : Stream
    {
        private readonly object _lock = new();
        private readonly List<byte> _raw = [];
        private readonly List<byte> _pending = [];
        private readonly List<NativeResponse> _responses = [];
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string RawText()
        {
            lock (_lock)
                return Encoding.UTF8.GetString(_raw.ToArray());
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
                _raw.AddRange(data);
                _pending.AddRange(data);
                var added = false;
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
}
