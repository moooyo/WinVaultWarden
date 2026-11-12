using App.Services;
using Xunit;

namespace App.Tests;

public sealed class BoundedImportReaderTests
{
    [Fact]
    public async Task ReadingAtTheBudgetPreservesBytesAndLeavesTheSourceOwnedByTheCaller()
    {
        using var source = new ObservingStream([1, 2, 3, 4]);

        var result = await BoundedImportReader.ReadAsync(source, CancellationToken.None, 4);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result);
        Assert.True(source.CanRead);
        Assert.True(IsCleared(source.LastBuffer));
    }

    [Fact]
    public async Task NonSeekableInputCannotExceedTheActualReadBudget()
    {
        using var source = new ObservingStream([1, 2, 3, 4, 5]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoundedImportReader.ReadAsync(source, CancellationToken.None, 4));

        Assert.True(IsCleared(source.LastBuffer));
    }

    [Fact]
    public async Task InitiallyCancelledReadDoesNotStartReadingTheSource()
    {
        using var source = new ObservingStream([1, 2, 3, 4]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedImportReader.ReadAsync(source, cancellation.Token, 4));

        Assert.Equal(0, source.ReadCalls);
    }

    [Fact]
    public async Task CancellationClearsBytesEvenWhenTheSourceCompletesLate()
    {
        using var source = new ObservingStream([1, 2, 3, 4]) { IgnoreCancellation = true, BlockFirstRead = true };
        using var cancellation = new CancellationTokenSource();
        var reading = BoundedImportReader.ReadAsync(source, cancellation.Token, 4);
        await source.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        source.ReleaseRead.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);

        Assert.True(IsCleared(source.LastBuffer));
    }

    private static bool IsCleared(Memory<byte> buffer)
    {
        foreach (var value in buffer.Span)
            if (value != 0) return false;
        return true;
    }

    private sealed class ObservingStream(byte[] content) : Stream
    {
        private int _offset;
        private bool _disposed;
        public bool IgnoreCancellation { get; init; }
        public bool BlockFirstRead { get; init; }
        public int ReadCalls { get; private set; }
        public Memory<byte> LastBuffer { get; private set; }
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReadCalls++;
            LastBuffer = buffer;
            ReadEntered.TrySetResult();
            if (ReadCalls == 1 && BlockFirstRead) await ReleaseRead.Task;
            if (!IgnoreCancellation) ct.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, content.Length - _offset);
            content.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public override void Flush() { }
        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
