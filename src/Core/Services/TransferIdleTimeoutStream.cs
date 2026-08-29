using System.Buffers;
using System.Security.Cryptography;

namespace Core.Services;

public enum TransferIoOperation
{
    OpenSource,
    Read,
    Write,
    Flush,
}

/// <summary>
/// Reports that one transfer I/O operation made no progress for the configured idle interval.
/// </summary>
public sealed class TransferIdleTimeoutException : TimeoutException
{
    public TransferIdleTimeoutException(TransferIoOperation operation, TimeSpan timeout)
        : base($"Transfer {operation} made no progress for {timeout}.")
    {
        Operation = operation;
        Timeout = timeout;
    }

    public TransferIoOperation Operation { get; }

    public TimeSpan Timeout { get; }
}

/// <summary>
/// Applies a fresh idle deadline to each asynchronous stream operation without owning the wrapped
/// stream. Private pooled buffers isolate the caller from an underlying operation that ignores
/// cancellation and completes after the timeout has already won.
/// </summary>
public sealed class TransferIdleTimeoutStream : Stream
{
    private static readonly Task Never = Task.Delay(Timeout.InfiniteTimeSpan);

    private readonly Stream _inner;
    private readonly TimeSpan _idleTimeout;
    private readonly LateOperationTracker _lateOperations = new();

    public TransferIdleTimeoutStream(Stream inner, ITransferIdleTimeoutPolicy timeoutPolicy)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(timeoutPolicy);
        _idleTimeout = timeoutPolicy.IdleTimeout;
        if (_idleTimeout != Timeout.InfiniteTimeSpan && _idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeoutPolicy));
    }

    public bool HasPendingOperations => _lateOperations.HasIncompleteOperations;

    public Task WaitForPendingOperationsAsync() => _lateOperations.WaitAsync();

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken ct) =>
        RunWithDeadlineAsync(
            token => _inner.FlushAsync(token),
            TransferIoOperation.Flush,
            ct);

    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken ct) =>
        ReadArrayAsync(buffer, offset, count, ct);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken ct = default) =>
        ReadMemoryAsync(buffer, ct);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) =>
        _inner.Write(buffer, offset, count);

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken ct) =>
        WriteArrayAsync(buffer, offset, count, ct);

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken ct = default) =>
        WriteMemoryAsync(buffer, ct);

    protected override void Dispose(bool disposing)
    {
        // The creator of the inner stream retains ownership.
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<int> ReadArrayAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("Offset and count exceed the destination buffer.");

        return await ReadMemoryAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
    }

    private async ValueTask<int> ReadMemoryAsync(Memory<byte> destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (destination.Length == 0)
            return 0;

        var privateBuffer = ArrayPool<byte>.Shared.Rent(destination.Length);
        var ownershipTransferred = false;
        try
        {
            var read = await RunWithDeadlineAsync(
                token => _inner.ReadAsync(privateBuffer.AsMemory(0, destination.Length), token).AsTask(),
                TransferIoOperation.Read,
                ct,
                operation =>
                {
                    ownershipTransferred = true;
                    var releaseTask = ReturnBufferAfterCompletionAsync(operation, privateBuffer);
                    _lateOperations.Track(releaseTask);
                    _ = releaseTask;
                }).ConfigureAwait(false);

            if ((uint)read > (uint)destination.Length)
                throw new InvalidDataException("The wrapped stream returned an invalid read count.");
            ct.ThrowIfCancellationRequested();
            privateBuffer.AsSpan(0, read).CopyTo(destination.Span);
            ct.ThrowIfCancellationRequested();
            return read;
        }
        finally
        {
            if (!ownershipTransferred)
                ReturnBuffer(privateBuffer);
        }
    }

    private Task WriteArrayAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("Offset and count exceed the source buffer.");

        return WriteMemoryAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }

    private async ValueTask WriteMemoryAsync(ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (source.Length == 0)
            return;

        var privateBuffer = ArrayPool<byte>.Shared.Rent(source.Length);
        var ownershipTransferred = false;
        try
        {
            source.Span.CopyTo(privateBuffer);
            await RunWithDeadlineAsync(
                async token =>
                {
                    await _inner.WriteAsync(privateBuffer.AsMemory(0, source.Length), token)
                        .ConfigureAwait(false);
                    return true;
                },
                TransferIoOperation.Write,
                ct,
                operation =>
                {
                    ownershipTransferred = true;
                    var releaseTask = ReturnBufferAfterCompletionAsync(operation, privateBuffer);
                    _lateOperations.Track(releaseTask);
                    _ = releaseTask;
                }).ConfigureAwait(false);
        }
        finally
        {
            if (!ownershipTransferred)
                ReturnBuffer(privateBuffer);
        }
    }

    private async Task RunWithDeadlineAsync(
        Func<CancellationToken, Task> operation,
        TransferIoOperation operationKind,
        CancellationToken callerToken)
    {
        await RunWithDeadlineAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            operationKind,
            callerToken,
            static _ => { }).ConfigureAwait(false);
    }

    private async Task<T> RunWithDeadlineAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TransferIoOperation operationKind,
        CancellationToken callerToken,
        Action<Task<T>> onAbandoned)
    {
        callerToken.ThrowIfCancellationRequested();
        CancellationTokenSource? operationCancellation = new();
        Task<T> operationTask;
        try
        {
            operationTask = operation(operationCancellation.Token)
                ?? throw new InvalidOperationException("The stream operation returned no task.");
        }
        catch
        {
            operationCancellation.Dispose();
            throw;
        }

        if (operationTask.IsCompleted)
        {
            operationCancellation.Dispose();
            callerToken.ThrowIfCancellationRequested();
            return await operationTask.ConfigureAwait(false);
        }

        var callerSignal = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerRegistration = callerToken.CanBeCanceled
            ? callerToken.UnsafeRegister(
                static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null),
                callerSignal)
            : default;
        using var timerCancellation = _idleTimeout == Timeout.InfiniteTimeSpan
            ? null
            : new CancellationTokenSource();
        var timeoutTask = timerCancellation is null
            ? Never
            : Task.Delay(_idleTimeout, timerCancellation.Token);

        try
        {
            var completed = await Task.WhenAny(operationTask, callerSignal.Task, timeoutTask)
                .ConfigureAwait(false);
            if (ReferenceEquals(completed, operationTask))
            {
                timerCancellation?.Cancel();
                operationCancellation.Dispose();
                operationCancellation = null;
                callerToken.ThrowIfCancellationRequested();
                return await operationTask.ConfigureAwait(false);
            }

            timerCancellation?.Cancel();
            onAbandoned(operationTask);
            var cancellationTask = operationCancellation.CancelAsync();
            var cleanupTask = DisposeCancellationAfterCompletionAsync(
                operationTask,
                cancellationTask,
                operationCancellation);
            _lateOperations.Track(operationTask);
            _ = ObserveAsync(cleanupTask);
            operationCancellation = null;

            callerToken.ThrowIfCancellationRequested();
            throw new TransferIdleTimeoutException(operationKind, _idleTimeout);
        }
        finally
        {
            operationCancellation?.Dispose();
        }
    }

    private static async Task DisposeCancellationAfterCompletionAsync(
        Task operation,
        Task cancellation,
        CancellationTokenSource source)
    {
        await ObserveAsync(operation).ConfigureAwait(false);
        await ObserveAsync(cancellation).ConfigureAwait(false);
        source.Dispose();
    }

    private static async Task ReturnBufferAfterCompletionAsync(Task operation, byte[] buffer)
    {
        await ObserveAsync(operation).ConfigureAwait(false);
        ReturnBuffer(buffer);
    }

    private static void ReturnBuffer(byte[] buffer)
    {
        CryptographicOperations.ZeroMemory(buffer);
        ArrayPool<byte>.Shared.Return(buffer);
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private sealed class LateOperationTracker
    {
        private readonly object _gate = new();
        private readonly HashSet<Task> _operations = [];

        public bool HasIncompleteOperations
        {
            get
            {
                lock (_gate)
                    return _operations.Count != 0;
            }
        }

        public void Track(Task operation)
        {
            var observed = ObserveAsync(operation);
            lock (_gate)
                _operations.Add(observed);
            _ = RemoveWhenCompletedAsync(observed);
        }

        public async Task WaitAsync()
        {
            while (true)
            {
                Task[] pending;
                lock (_gate)
                    pending = _operations.ToArray();
                if (pending.Length == 0)
                    return;
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
        }

        private async Task RemoveWhenCompletedAsync(Task operation)
        {
            await operation.ConfigureAwait(false);
            lock (_gate)
                _operations.Remove(operation);
        }
    }
}
