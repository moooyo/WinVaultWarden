using Core.Services;
using Xunit;

namespace Crypto.Tests;

public sealed class TransferIdleTimeoutStreamTests
{
    [Fact]
    public async Task ReadTimeout_IgnoringCancellation_KeepsPrivateBufferUntilLateCompletion()
    {
        var inner = new LateReadStream();
        var timed = new TransferIdleTimeoutStream(inner, Policy(TimeSpan.FromMilliseconds(25)));
        var callerBuffer = new byte[32];

        var readTask = timed.ReadAsync(callerBuffer, CancellationToken.None).AsTask();
        await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<TransferIdleTimeoutException>(() =>
            readTask.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(TransferIoOperation.Read, error.Operation);
        Assert.All(callerBuffer, value => Assert.Equal(0, value));
        Assert.All(inner.Captured.ToArray(), value => Assert.Equal(0xA5, value));
        Assert.True(timed.HasPendingOperations);

        inner.Complete(1);
        await timed.WaitForPendingOperationsAsync()
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(timed.HasPendingOperations);
        Assert.All(inner.Captured.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task WriteTimeout_IgnoringCancellation_CopiesAndRetainsPrivateBufferUntilCompletion()
    {
        var inner = new LateWriteStream();
        var timed = new TransferIdleTimeoutStream(inner, Policy(TimeSpan.FromMilliseconds(25)));
        var callerBuffer = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var expected = callerBuffer.ToArray();

        var writeTask = timed.WriteAsync(callerBuffer, CancellationToken.None).AsTask();
        await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Array.Clear(callerBuffer);
        var error = await Assert.ThrowsAsync<TransferIdleTimeoutException>(() =>
            writeTask.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(TransferIoOperation.Write, error.Operation);
        Assert.Equal(expected, inner.Captured.ToArray());
        Assert.True(timed.HasPendingOperations);

        inner.Complete();
        await timed.WaitForPendingOperationsAsync()
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(timed.HasPendingOperations);
        Assert.All(inner.Captured.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task CallerCancellation_IgnoringRead_WinsWithoutReturningPrivateBufferEarly()
    {
        var inner = new LateReadStream();
        var timed = new TransferIdleTimeoutStream(inner, Policy(Timeout.InfiniteTimeSpan));
        using var caller = new CancellationTokenSource();
        var readTask = timed.ReadAsync(new byte[16], caller.Token).AsTask();
        await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            readTask.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.All(inner.Captured.ToArray(), value => Assert.Equal(0xA5, value));
        inner.Complete(0);
        await timed.WaitForPendingOperationsAsync()
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(timed.HasPendingOperations);
    }

    [Fact]
    public async Task FlushTimeout_IgnoringCancellation_IsBoundedAndTracked()
    {
        var inner = new LateFlushStream();
        var timed = new TransferIdleTimeoutStream(inner, Policy(TimeSpan.FromMilliseconds(25)));

        var flushTask = timed.FlushAsync(CancellationToken.None);
        await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<TransferIdleTimeoutException>(() =>
            flushTask.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(TransferIoOperation.Flush, error.Operation);
        Assert.True(timed.HasPendingOperations);
        inner.Complete();
        await timed.WaitForPendingOperationsAsync()
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(timed.HasPendingOperations);
    }

    [Fact]
    public async Task PerOperationProgress_CanContinueBeyondOneIdleInterval()
    {
        var payload = Enumerable.Range(0, 20).Select(value => (byte)value).ToArray();
        await using var inner = new SlowProgressReadStream(payload, TimeSpan.FromMilliseconds(20));
        var timed = new TransferIdleTimeoutStream(inner, Policy(TimeSpan.FromMilliseconds(250)));
        var actual = new List<byte>();
        var buffer = new byte[1];

        while (await timed.ReadAsync(buffer, CancellationToken.None) != 0)
            actual.Add(buffer[0]);

        Assert.Equal(payload, actual);
    }

    private static ITransferIdleTimeoutPolicy Policy(TimeSpan timeout) =>
        new TransferIdleTimeoutPolicy(timeout);

    private sealed class LateReadStream : Stream
    {
        private readonly TaskCompletionSource<int> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Memory<byte> Captured { get; private set; }

        public void Complete(int read) => _completion.TrySetResult(read);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Captured = buffer;
            buffer.Span.Fill(0xA5);
            Entered.TrySetResult(null);
            return new ValueTask<int>(_completion.Task);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class LateWriteStream : Stream
    {
        private readonly TaskCompletionSource<object?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ReadOnlyMemory<byte> Captured { get; private set; }

        public void Complete() => _completion.TrySetResult(null);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Captured = buffer;
            Entered.TrySetResult(null);
            return new ValueTask(_completion.Task);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class LateFlushStream : Stream
    {
        private readonly TaskCompletionSource<object?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete() => _completion.TrySetResult(null);

        public override Task FlushAsync(CancellationToken ct)
        {
            Entered.TrySetResult(null);
            return _completion.Task;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SlowProgressReadStream(byte[] payload, TimeSpan delay) : Stream
    {
        private int _offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_offset == payload.Length)
                return 0;
            await Task.Delay(delay, ct);
            buffer.Span[0] = payload[_offset++];
            return 1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.LongLength;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
