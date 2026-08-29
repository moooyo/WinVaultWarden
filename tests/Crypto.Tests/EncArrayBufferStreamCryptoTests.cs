using System.Security.Cryptography;
using Core.Models;
using Core.Services;
using Crypto;
using Xunit;

namespace Crypto.Tests;

public sealed class EncArrayBufferStreamCryptoTests
{
    [Fact]
    public async Task DecryptAsync_ValidPayload_RoundTripsAndRemovesTemporaryFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var key = NewKey();
        var plaintext = RandomNumberGenerator.GetBytes(200_123);
        var encoded = Encrypt(plaintext, key);
        var crypto = new EncArrayBufferStreamCrypto(temporaryDirectory.Path);
        await using var source = new FragmentedNonSeekableStream(encoded, 37);
        await using var destination = new MemoryStream();

        var written = await crypto.DecryptAsync(
            source,
            destination,
            key,
            encoded.LongLength,
            plaintext.LongLength,
            encoded.LongLength,
            CancellationToken.None);

        Assert.Equal(plaintext.LongLength, written);
        Assert.Equal(plaintext, destination.ToArray());
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory.Path));
    }

    [Fact]
    public async Task DecryptAsync_OneByteNonSeekableSource_RoundTrips()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var key = NewKey();
        var plaintext = RandomNumberGenerator.GetBytes(257);
        var encoded = Encrypt(plaintext, key);
        var crypto = new EncArrayBufferStreamCrypto(temporaryDirectory.Path);
        await using var source = new FragmentedNonSeekableStream(encoded, 1);
        await using var destination = new MemoryStream();

        Assert.False(source.CanSeek);
        var written = await crypto.DecryptAsync(
            source,
            destination,
            key,
            encoded.LongLength,
            plaintext.LongLength,
            encoded.LongLength,
            CancellationToken.None);

        Assert.Equal(plaintext.LongLength, written);
        Assert.Equal(plaintext, destination.ToArray());
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory.Path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(49)]
    public async Task DecryptAsync_AuthenticatedBytesTampered_NeverWritesPlaintextAndRemovesTemporaryFile(
        int offset)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var key = NewKey();
        var encoded = Encrypt(RandomNumberGenerator.GetBytes(4097), key);
        encoded[offset] ^= 0x80;
        var crypto = new EncArrayBufferStreamCrypto(temporaryDirectory.Path);
        await using var source = new MemoryStream(encoded, writable: false);
        await using var destination = new WriteTrackingStream();

        await Assert.ThrowsAsync<CryptographicException>(() => crypto.DecryptAsync(
            source,
            destination,
            key,
            encoded.LongLength,
            4097,
            encoded.LongLength,
            CancellationToken.None));

        Assert.Equal(0, destination.WriteCalls);
        Assert.Equal(0, destination.Length);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory.Path));
    }

    [Fact]
    public async Task DecryptAsync_WrongTypeOrTruncatedHeader_NeverWritesPlaintext()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var key = NewKey();
        var crypto = new EncArrayBufferStreamCrypto(temporaryDirectory.Path);
        await using var destination = new WriteTrackingStream();
        var wrongType = new byte[EncArrayBuffer.MinimumEncodedLength];
        wrongType[0] = 1;

        await Assert.ThrowsAsync<CryptographicException>(() => crypto.DecryptAsync(
            new MemoryStream(wrongType, writable: false),
            destination,
            key,
            wrongType.LongLength,
            CancellationToken.None));

        await Assert.ThrowsAsync<CryptographicException>(() => crypto.DecryptAsync(
            new MemoryStream(new byte[EncArrayBuffer.MetadataLength - 1], writable: false),
            destination,
            key,
            EncArrayBuffer.MinimumEncodedLength,
            CancellationToken.None));

        Assert.Equal(0, destination.WriteCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory.Path));
    }

    [Fact]
    public async Task DecryptAsync_AuthenticatedPlaintextExceedsLimit_FailsAndRemovesTemporaryFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var key = NewKey();
        var plaintext = RandomNumberGenerator.GetBytes(4097);
        var encoded = Encrypt(plaintext, key);
        var crypto = new EncArrayBufferStreamCrypto(temporaryDirectory.Path);
        await using var source = new MemoryStream(encoded, writable: false);
        await using var destination = new WriteTrackingStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => crypto.DecryptAsync(
            source,
            destination,
            key,
            encoded.LongLength,
            plaintext.LongLength - 1,
            encoded.LongLength,
            CancellationToken.None));

        Assert.True(destination.Length <= plaintext.LongLength - 1);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory.Path));
    }

    [Fact]
    public void DomainLimits_MapToExactEncodedLengths()
    {
        Assert.Equal(524_288_065, SendCryptoService.MaxEncryptedFileBytes);
        Assert.Equal(104_857_665, AttachmentCryptoService.MaxEncryptedFileBytes);
    }

    [Theory]
    [InlineData(-16)]
    [InlineData(16)]
    public async Task DecryptAsync_DeclaredLengthMismatch_FailsBeforePlaintext(int delta)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var key = NewKey();
        var encoded = Encrypt(RandomNumberGenerator.GetBytes(1024), key);
        var declaredLength = encoded.LongLength + delta;
        var crypto = new EncArrayBufferStreamCrypto(temporaryDirectory.Path);
        await using var source = new MemoryStream(encoded, writable: false);
        await using var destination = new WriteTrackingStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => crypto.DecryptAsync(
            source,
            destination,
            key,
            encoded.LongLength + 32,
            1024,
            declaredLength,
            CancellationToken.None));

        Assert.Equal(0, destination.WriteCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory.Path));
    }

    [Fact]
    public async Task DecryptAsync_ActualBytesExceedLimit_FailsBeforePlaintext()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var key = NewKey();
        var encoded = Encrypt(RandomNumberGenerator.GetBytes(1024), key);
        var crypto = new EncArrayBufferStreamCrypto(temporaryDirectory.Path);
        await using var source = new MemoryStream(encoded, writable: false);
        await using var destination = new WriteTrackingStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => crypto.DecryptAsync(
            source,
            destination,
            key,
            encoded.LongLength - EncArrayBuffer.CipherBlockLength,
            CancellationToken.None));

        Assert.Equal(0, destination.WriteCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    public async Task DecryptAsync_InvalidCiphertextLength_FailsBeforeMacOrPlaintext(int ciphertextLength)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var key = NewKey();
        var encoded = new byte[EncArrayBuffer.MetadataLength + ciphertextLength];
        encoded[0] = EncArrayBuffer.EncryptionTypeByte;
        var crypto = new EncArrayBufferStreamCrypto(temporaryDirectory.Path);
        await using var source = new MemoryStream(encoded, writable: false);
        await using var destination = new WriteTrackingStream();

        await Assert.ThrowsAsync<CryptographicException>(() => crypto.DecryptAsync(
            source,
            destination,
            key,
            EncArrayBuffer.MinimumEncodedLength + 32,
            CancellationToken.None));

        Assert.Equal(0, destination.WriteCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory.Path));
    }

    [Fact]
    public async Task DecryptAsync_CancellationAfterHeader_RemovesTemporaryFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var key = NewKey();
        var encoded = Encrypt(RandomNumberGenerator.GetBytes(4096), key);
        using var cancellation = new CancellationTokenSource();
        var crypto = new EncArrayBufferStreamCrypto(temporaryDirectory.Path);
        await using var source = new CancelAfterHeaderStream(encoded, cancellation);
        await using var destination = new WriteTrackingStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => crypto.DecryptAsync(
            source,
            destination,
            key,
            encoded.LongLength,
            4096,
            encoded.LongLength,
            cancellation.Token));

        Assert.Equal(0, destination.WriteCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory.Path));
    }

    [Fact]
    public async Task PrepareUploadAsync_SameLengthMutation_FailsClosed()
    {
        var first = RandomNumberGenerator.GetBytes(1024);
        var second = RandomNumberGenerator.GetBytes(1024);
        var openCount = 0;
        var source = new ReopenableFileSource("changing.bin", first.LongLength, _ =>
        {
            var content = Interlocked.Increment(ref openCount) == 1 ? first : second;
            return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
        });
        using var key = NewKey();
        var crypto = new EncArrayBufferStreamCrypto();

        var upload = await crypto.PrepareUploadAsync(source, key, first.LongLength, CancellationToken.None);
        await using var destination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            upload.WriteAsync(destination, CancellationToken.None));

        Assert.Equal(2, openCount);
    }

    [Fact]
    public async Task PrepareUploadAsync_SourceReadIgnoresCancellation_TimesOutBeforeSourceDisposal()
    {
        var stalledSource = new LateOwnedReadStream();
        var source = new ReopenableFileSource(
            "stalled.bin",
            1,
            _ => ValueTask.FromResult<Stream>(stalledSource));
        using var key = NewKey();
        var crypto = new EncArrayBufferStreamCrypto();
        var policy = new TransferIdleTimeoutPolicy(TimeSpan.FromMilliseconds(25));

        var prepareTask = crypto.PrepareUploadAsync(source, key, 1, policy, CancellationToken.None);
        await stalledSource.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<TransferIdleTimeoutException>(() =>
            prepareTask.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(TransferIoOperation.Read, error.Operation);
        Assert.False(stalledSource.Disposed.Task.IsCompleted);
        Assert.All(stalledSource.Captured.ToArray(), value => Assert.Equal(0xA5, value));

        stalledSource.Complete(1);
        await stalledSource.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task PrepareUploadAsync_OpenIgnoresCancellation_DisposesLateStreamResult()
    {
        var openCompletion = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var openEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateStream = new DisposeTrackingStream();
        var source = new ReopenableFileSource("late-open.bin", 1, _ =>
        {
            openEntered.TrySetResult(null);
            return new ValueTask<Stream>(openCompletion.Task);
        });
        using var key = NewKey();
        var crypto = new EncArrayBufferStreamCrypto();
        var policy = new TransferIdleTimeoutPolicy(TimeSpan.FromMilliseconds(25));

        var prepareTask = crypto.PrepareUploadAsync(source, key, 1, policy, CancellationToken.None);
        await openEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<TransferIdleTimeoutException>(() =>
            prepareTask.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(TransferIoOperation.OpenSource, error.Operation);
        openCompletion.TrySetResult(lateStream);
        await lateStream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task PreparedUpload_SecondPassSourceReadIgnoresCancellation_TimesOutBeforeDisposal()
    {
        var stalledSecondPass = new LateOwnedReadStream();
        var openCount = 0;
        var source = new ReopenableFileSource("second-pass.bin", 1, _ =>
        {
            openCount++;
            return ValueTask.FromResult<Stream>(openCount == 1
                ? new MemoryStream([0x5A], writable: false)
                : stalledSecondPass);
        });
        using var key = NewKey();
        var crypto = new EncArrayBufferStreamCrypto();
        var policy = new TransferIdleTimeoutPolicy(TimeSpan.FromMilliseconds(25));
        var upload = await crypto.PrepareUploadAsync(source, key, 1, policy, CancellationToken.None);
        await using var destination = new MemoryStream();

        var writeTask = upload.WriteAsync(destination, CancellationToken.None);
        await stalledSecondPass.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<TransferIdleTimeoutException>(() =>
            writeTask.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(TransferIoOperation.Read, error.Operation);
        Assert.Equal(2, openCount);
        Assert.False(stalledSecondPass.Disposed.Task.IsCompleted);
        Assert.All(stalledSecondPass.Captured.ToArray(), value => Assert.Equal(0xA5, value));

        stalledSecondPass.Complete(1);
        await stalledSecondPass.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task PreparedUpload_DestinationWriteIgnoresCancellation_KeepsCopiedBytesUntilCompletion()
    {
        var plaintext = RandomNumberGenerator.GetBytes(1024);
        var source = new ReopenableFileSource(
            "upload.bin",
            plaintext.LongLength,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(plaintext, writable: false)));
        using var key = NewKey();
        var crypto = new EncArrayBufferStreamCrypto();
        var policy = new TransferIdleTimeoutPolicy(TimeSpan.FromMilliseconds(25));
        var upload = await crypto.PrepareUploadAsync(source, key, plaintext.LongLength, policy, CancellationToken.None);
        var destination = new LateWriteDestination();

        var writeTask = upload.WriteAsync(destination, CancellationToken.None);
        await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var expected = destination.Captured.ToArray();
        var error = await Assert.ThrowsAsync<TransferIdleTimeoutException>(() =>
            writeTask.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(TransferIoOperation.Write, error.Operation);
        Assert.Equal(expected, destination.Captured.ToArray());
        destination.Complete();
    }

    [Fact]
    public async Task DecryptAsync_TemporaryWriteIgnoresCancellation_DefersTemporaryDisposal()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var key = NewKey();
        var plaintext = RandomNumberGenerator.GetBytes(1024);
        var encoded = Encrypt(plaintext, key);
        var temporary = new ControllableTemporaryStream(blockWrite: true, blockFlush: false);
        var crypto = new EncArrayBufferStreamCrypto(temporaryDirectory.Path, _ => temporary);
        var policy = new TransferIdleTimeoutPolicy(TimeSpan.FromMilliseconds(25));
        await using var source = new MemoryStream(encoded, writable: false);
        await using var destination = new MemoryStream();

        var decryptTask = crypto.DecryptAsync(
            source,
            destination,
            key,
            encoded.LongLength,
            plaintext.LongLength,
            encoded.LongLength,
            policy,
            CancellationToken.None);
        await temporary.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var expected = temporary.CapturedWrite.ToArray();
        var error = await Assert.ThrowsAsync<TransferIdleTimeoutException>(() =>
            decryptTask.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(TransferIoOperation.Write, error.Operation);
        Assert.Empty(destination.ToArray());
        Assert.False(temporary.Disposed.Task.IsCompleted);
        Assert.Equal(expected, temporary.CapturedWrite.ToArray());

        temporary.CompleteWrite();
        await temporary.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DecryptAsync_TemporaryFlushIgnoresCancellation_DefersTemporaryDisposal()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var key = NewKey();
        var plaintext = RandomNumberGenerator.GetBytes(1024);
        var encoded = Encrypt(plaintext, key);
        var temporary = new ControllableTemporaryStream(blockWrite: false, blockFlush: true);
        var crypto = new EncArrayBufferStreamCrypto(temporaryDirectory.Path, _ => temporary);
        var policy = new TransferIdleTimeoutPolicy(TimeSpan.FromMilliseconds(25));
        await using var source = new MemoryStream(encoded, writable: false);
        await using var destination = new MemoryStream();

        var decryptTask = crypto.DecryptAsync(
            source,
            destination,
            key,
            encoded.LongLength,
            plaintext.LongLength,
            encoded.LongLength,
            policy,
            CancellationToken.None);
        await temporary.FlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<TransferIdleTimeoutException>(() =>
            decryptTask.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(TransferIoOperation.Flush, error.Operation);
        Assert.Empty(destination.ToArray());
        Assert.False(temporary.Disposed.Task.IsCompleted);

        temporary.CompleteFlush();
        await temporary.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DecryptAsync_PlaintextDestinationIgnoresCancellation_TimesOutWithStableCopy()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var key = NewKey();
        var plaintext = RandomNumberGenerator.GetBytes(1024);
        var encoded = Encrypt(plaintext, key);
        var crypto = new EncArrayBufferStreamCrypto(temporaryDirectory.Path);
        var policy = new TransferIdleTimeoutPolicy(TimeSpan.FromMilliseconds(25));
        await using var source = new MemoryStream(encoded, writable: false);
        var destination = new LateWriteDestination();

        var decryptTask = crypto.DecryptAsync(
            source,
            destination,
            key,
            encoded.LongLength,
            plaintext.LongLength,
            encoded.LongLength,
            policy,
            CancellationToken.None);
        await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var expected = destination.Captured.ToArray();
        var error = await Assert.ThrowsAsync<TransferIdleTimeoutException>(() =>
            decryptTask.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(TransferIoOperation.Write, error.Operation);
        Assert.Equal(expected, destination.Captured.ToArray());
        destination.Complete();
    }

    [Fact]
    public async Task AttachmentStreamingMethods_RoundTripWithoutWholeFileBuffers()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var key = NewKey();
        var plaintext = RandomNumberGenerator.GetBytes(123_457);
        var source = new ReopenableFileSource(
            "attachment.bin",
            plaintext.LongLength,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(plaintext, writable: false)));
        var service = new AttachmentCryptoService(
            new CryptoService(),
            new EncArrayBufferStreamCrypto(temporaryDirectory.Path));

        var upload = await service.PrepareFileUploadAsync(source, key, CancellationToken.None);
        await using var encoded = new MemoryStream();
        await upload.WriteAsync(encoded, CancellationToken.None);
        encoded.Position = 0;
        await using var destination = new MemoryStream();

        var written = await service.DecryptFileAsync(
            encoded,
            destination,
            key,
            upload.Length,
            CancellationToken.None);

        Assert.Equal(plaintext.LongLength, written);
        Assert.Equal(plaintext, destination.ToArray());
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory.Path));
    }

    private static SymmetricCryptoKey NewKey() =>
        new(RandomNumberGenerator.GetBytes(64));

    private static byte[] Encrypt(byte[] plaintext, SymmetricCryptoKey key) =>
        EncArrayBuffer.Pack(new CryptoService().Encrypt(plaintext, key));

    private sealed class FragmentedNonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _chunkSize;

        public FragmentedNonSeekableStream(byte[] content, int chunkSize)
        {
            _inner = new MemoryStream(content, writable: false);
            _chunkSize = chunkSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, _chunkSize));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, _chunkSize)], ct);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    private sealed class CancelAfterHeaderStream(byte[] content, CancellationTokenSource cancellation)
        : MemoryStream(content, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position >= EncArrayBuffer.MetadataLength)
                cancellation.Cancel();
            return base.ReadAsync(buffer, ct);
        }
    }

    private sealed class WriteTrackingStream : MemoryStream
    {
        public int WriteCalls { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            WriteCalls++;
            return base.WriteAsync(buffer, ct);
        }
    }

    private sealed class LateOwnedReadStream : Stream
    {
        private readonly TaskCompletionSource<int> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> Disposed { get; } =
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

        public override ValueTask DisposeAsync()
        {
            Disposed.TrySetResult(null);
            return ValueTask.CompletedTask;
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

    private sealed class DisposeTrackingStream : MemoryStream
    {
        public TaskCompletionSource<object?> Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask DisposeAsync()
        {
            base.Dispose();
            Disposed.TrySetResult(null);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LateWriteDestination : Stream
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

    private sealed class ControllableTemporaryStream(bool blockWrite, bool blockFlush) : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly TaskCompletionSource<object?> _writeCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> _flushCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> WriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> FlushEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ReadOnlyMemory<byte> CapturedWrite { get; private set; }

        public void CompleteWrite() => _writeCompletion.TrySetResult(null);

        public void CompleteFlush() => _flushCompletion.TrySetResult(null);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (!blockWrite)
            {
                await _inner.WriteAsync(buffer, ct);
                return;
            }

            CapturedWrite = buffer;
            WriteEntered.TrySetResult(null);
            await _writeCompletion.Task;
        }

        public override Task FlushAsync(CancellationToken ct)
        {
            if (!blockFlush)
                return Task.CompletedTask;
            FlushEntered.TrySetResult(null);
            return _flushCompletion.Task;
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            Disposed.TrySetResult(null);
            GC.SuppressFinalize(this);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _inner.ReadAsync(buffer, ct);
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"WinVaultWarden.Crypto.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
