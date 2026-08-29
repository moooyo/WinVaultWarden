using System.Buffers;
using System.Security.Cryptography;
using Core.Models;
using Core.Services;

namespace Crypto;

/// <summary>
/// Constant-memory authenticated streaming for Bitwarden EncArrayBuffer file payloads.
/// </summary>
public sealed class EncArrayBufferStreamCrypto
{
    private const int CopyBufferSize = 81_920;
    private static readonly Task NeverSignal = Task.Delay(Timeout.InfiniteTimeSpan);
    private static readonly ITransferIdleTimeoutPolicy DefaultTimeoutPolicy =
        new TransferIdleTimeoutPolicy(TransferIdleTimeoutPolicy.DefaultIdleTimeout);
    private readonly string _temporaryDirectory;
    private readonly Func<string, Stream> _temporaryStreamFactory;

    public EncArrayBufferStreamCrypto(string? temporaryDirectory = null)
        : this(temporaryDirectory, CreateTemporaryCiphertextStream)
    {
    }

    internal EncArrayBufferStreamCrypto(
        string? temporaryDirectory,
        Func<string, Stream> temporaryStreamFactory)
    {
        _temporaryDirectory = Path.GetFullPath(temporaryDirectory ?? Path.GetTempPath());
        _temporaryStreamFactory = temporaryStreamFactory
            ?? throw new ArgumentNullException(nameof(temporaryStreamFactory));
    }

    /// <summary>
    /// Prepares the two-pass upload required by the EncArrayBuffer wire layout. The first pass
    /// computes the MAC and the second pass writes the header and ciphertext while recomputing the
    /// MAC. A same-length source mutation between passes therefore fails closed.
    /// </summary>
    public Task<StreamUploadContent> PrepareUploadAsync(
        ReopenableFileSource source,
        SymmetricCryptoKey key,
        long maxPlaintextBytes,
        CancellationToken ct = default) =>
        PrepareUploadAsync(source, key, maxPlaintextBytes, DefaultTimeoutPolicy, ct);

    public async Task<StreamUploadContent> PrepareUploadAsync(
        ReopenableFileSource source,
        SymmetricCryptoKey key,
        long maxPlaintextBytes,
        ITransferIdleTimeoutPolicy timeoutPolicy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(source.OpenReadAsync);
        ArgumentNullException.ThrowIfNull(timeoutPolicy);
        ReopenableFileSource.ValidateLength(source.Length, maxPlaintextBytes);
        EnsureAuthenticatedKey(key);
        ct.ThrowIfCancellationRequested();

        var iv = RandomNumberGenerator.GetBytes(EncArrayBuffer.IvLength);
        byte[]? mac = null;
        try
        {
            var authenticatedMac = await ComputeMacAsync(source, key, iv, timeoutPolicy, ct)
                .ConfigureAwait(false);
            mac = authenticatedMac;
            var encryptedLength = EncArrayBuffer.GetEncodedLength(source.Length);

            return new StreamUploadContent(
                encryptedLength,
                async (destination, writeCt) =>
                {
                    ArgumentNullException.ThrowIfNull(destination);
                    if (!destination.CanWrite)
                        throw new ArgumentException("The upload destination must be writable.", nameof(destination));

                    var timedDestination = new TransferIdleTimeoutStream(destination, timeoutPolicy);
                    await timedDestination.WriteAsync(new[] { EncArrayBuffer.EncryptionTypeByte }, writeCt)
                        .ConfigureAwait(false);
                    await timedDestination.WriteAsync(iv, writeCt).ConfigureAwait(false);
                    await timedDestination.WriteAsync(authenticatedMac, writeCt).ConfigureAwait(false);

                    using var verification = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key.MacKey!);
                    verification.AppendData(iv);
                    await using var plaintext = await OpenSourceAsync(source, timeoutPolicy, writeCt)
                        .ConfigureAwait(false);
                    using var aes = CreateAes(key, iv);
                    await using (var tee = new HmacTeeStream(timedDestination, verification))
                    await using (var encrypting = new CryptoStream(
                        tee,
                        aes.CreateEncryptor(),
                        CryptoStreamMode.Write,
                        leaveOpen: true))
                    {
                        await CopyExactlyAsync(plaintext.Stream, encrypting, source.Length, writeCt)
                            .ConfigureAwait(false);
                        await encrypting.FlushFinalBlockAsync(writeCt).ConfigureAwait(false);
                    }

                    var secondPassMac = verification.GetHashAndReset();
                    try
                    {
                        if (!CryptographicOperations.FixedTimeEquals(authenticatedMac, secondPassMac))
                        {
                            throw new InvalidDataException(
                                "File content changed between encryption passes; refusing to upload mismatched ciphertext.");
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(secondPassMac);
                    }
                });
        }
        catch
        {
            CryptographicOperations.ZeroMemory(iv);
            if (mac is not null)
                CryptographicOperations.ZeroMemory(mac);
            throw;
        }
    }

    public Task<long> DecryptAsync(
        Stream encryptedSource,
        Stream plaintextDestination,
        SymmetricCryptoKey key,
        long maxEncryptedBytes,
        CancellationToken ct = default) =>
        DecryptAsync(
            encryptedSource,
            plaintextDestination,
            key,
            maxEncryptedBytes,
            maxEncryptedBytes - EncArrayBuffer.MetadataLength,
            null,
            DefaultTimeoutPolicy,
            ct);

    /// <summary>
    /// Authenticates an EncArrayBuffer before releasing any plaintext. Ciphertext is bounded by
    /// bytes actually read and staged in a private delete-on-close file while its HMAC is computed.
    /// Only a fixed-time MAC match permits streaming AES-CBC/PKCS7 decryption to the destination.
    /// Callers that require atomic behavior for cancellation or destination failures after
    /// authentication must supply a transactional destination stream.
    /// </summary>
    /// <returns>The number of plaintext bytes written.</returns>
    public Task<long> DecryptAsync(
        Stream encryptedSource,
        Stream plaintextDestination,
        SymmetricCryptoKey key,
        long maxEncryptedBytes,
        long maxPlaintextBytes,
        long? declaredLength,
        CancellationToken ct = default) =>
        DecryptAsync(
            encryptedSource,
            plaintextDestination,
            key,
            maxEncryptedBytes,
            maxPlaintextBytes,
            declaredLength,
            DefaultTimeoutPolicy,
            ct);

    public async Task<long> DecryptAsync(
        Stream encryptedSource,
        Stream plaintextDestination,
        SymmetricCryptoKey key,
        long maxEncryptedBytes,
        long maxPlaintextBytes,
        long? declaredLength,
        ITransferIdleTimeoutPolicy timeoutPolicy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(encryptedSource);
        ArgumentNullException.ThrowIfNull(plaintextDestination);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(timeoutPolicy);
        if (!encryptedSource.CanRead)
            throw new ArgumentException("The encrypted source must be readable.", nameof(encryptedSource));
        if (!plaintextDestination.CanWrite)
            throw new ArgumentException("The plaintext destination must be writable.", nameof(plaintextDestination));
        if (ReferenceEquals(encryptedSource, plaintextDestination))
            throw new ArgumentException("Encrypted source and plaintext destination must be different streams.");
        if (maxEncryptedBytes < EncArrayBuffer.MinimumEncodedLength)
            throw new ArgumentOutOfRangeException(nameof(maxEncryptedBytes));
        if (maxPlaintextBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maxPlaintextBytes));
        EnsureAuthenticatedKey(key);
        ValidateDeclaredLength(declaredLength, maxEncryptedBytes);

        var header = new byte[EncArrayBuffer.MetadataLength];
        byte[]? iv = null;
        byte[]? expectedMac = null;
        TemporaryCiphertextLease? temporaryCiphertext = null;
        try
        {
            var timedEncryptedSource = new TransferIdleTimeoutStream(encryptedSource, timeoutPolicy);
            var timedPlaintextDestination = new TransferIdleTimeoutStream(plaintextDestination, timeoutPolicy);
            await ReadExactlyAsync(timedEncryptedSource, header, ct).ConfigureAwait(false);
            if (header[0] != EncArrayBuffer.EncryptionTypeByte)
            {
                throw new CryptographicException(
                    $"Unexpected EncArrayBuffer encryption type: {header[0]}.");
            }

            iv = header[EncArrayBuffer.TypeLength..(EncArrayBuffer.TypeLength + EncArrayBuffer.IvLength)];
            expectedMac = header[(EncArrayBuffer.TypeLength + EncArrayBuffer.IvLength)..EncArrayBuffer.MetadataLength];

            temporaryCiphertext = OpenTemporaryCiphertext(timeoutPolicy);
            using var authentication = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key.MacKey!);
            authentication.AppendData(iv);
            var ciphertextLength = await CopyCiphertextForAuthenticationAsync(
                timedEncryptedSource,
                temporaryCiphertext.Stream,
                authentication,
                maxEncryptedBytes,
                declaredLength,
                ct).ConfigureAwait(false);

            var actualLength = checked(EncArrayBuffer.MetadataLength + ciphertextLength);
            if (declaredLength is long declared && declared != actualLength)
            {
                throw new InvalidDataException(
                    $"Declared encrypted length {declared} does not match actual length {actualLength}.");
            }

            EncArrayBuffer.ValidateCiphertextLength(ciphertextLength);
            await temporaryCiphertext.Stream.FlushAsync(ct).ConfigureAwait(false);

            var computedMac = authentication.GetHashAndReset();
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expectedMac, computedMac))
                    throw new CryptographicException("EncArrayBuffer MAC validation failed.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(computedMac);
            }

            temporaryCiphertext.Stream.Position = 0;
            using var aes = CreateAes(key, iv);
            await using var decrypting = new CryptoStream(
                temporaryCiphertext.Stream,
                aes.CreateDecryptor(),
                CryptoStreamMode.Read,
                leaveOpen: true);
            return await CopyToAsync(
                decrypting,
                timedPlaintextDestination,
                maxPlaintextBytes,
                ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
            if (iv is not null)
                CryptographicOperations.ZeroMemory(iv);
            if (expectedMac is not null)
                CryptographicOperations.ZeroMemory(expectedMac);
            if (temporaryCiphertext is not null)
                await temporaryCiphertext.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ComputeMacAsync(
        ReopenableFileSource source,
        SymmetricCryptoKey key,
        byte[] iv,
        ITransferIdleTimeoutPolicy timeoutPolicy,
        CancellationToken ct)
    {
        using var authentication = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key.MacKey!);
        authentication.AppendData(iv);
        await using var plaintext = await OpenSourceAsync(source, timeoutPolicy, ct).ConfigureAwait(false);
        using var aes = CreateAes(key, iv);
        await using var sink = new IncrementalHashWriteStream(authentication);
        await using var encrypting = new CryptoStream(
            sink,
            aes.CreateEncryptor(),
            CryptoStreamMode.Write,
            leaveOpen: true);
        await CopyExactlyAsync(plaintext.Stream, encrypting, source.Length, ct).ConfigureAwait(false);
        await encrypting.FlushFinalBlockAsync(ct).ConfigureAwait(false);
        return authentication.GetHashAndReset();
    }

    private static async Task<long> CopyCiphertextForAuthenticationAsync(
        Stream source,
        Stream temporaryCiphertext,
        IncrementalHash authentication,
        long maxEncryptedBytes,
        long? declaredLength,
        CancellationToken ct)
    {
        var maxCiphertextBytes = maxEncryptedBytes - EncArrayBuffer.MetadataLength;
        var declaredCiphertextBytes = declaredLength is long declared
            ? declared - EncArrayBuffer.MetadataLength
            : (long?)null;
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                if (read > maxCiphertextBytes - total)
                    throw new InvalidDataException($"Encrypted payload exceeds the {maxEncryptedBytes}-byte limit.");
                if (declaredCiphertextBytes is long declaredBody && read > declaredBody - total)
                    throw new InvalidDataException("Encrypted payload exceeds its declared length.");

                authentication.AppendData(buffer, 0, read);
                await temporaryCiphertext.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
            }

            return total;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        long expectedLength,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                total = checked(total + read);
                if (total > expectedLength)
                    throw new InvalidDataException("File grew after it was selected.");
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            if (total != expectedLength)
                throw new InvalidDataException("File length changed after it was selected.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<long> CopyToAsync(
        Stream source,
        Stream destination,
        long maxPlaintextBytes,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0)
                    return total;
                if (read > maxPlaintextBytes - total)
                    throw new InvalidDataException($"Plaintext exceeds the {maxPlaintextBytes}-byte limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total = checked(total + read);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReadExactlyAsync(Stream source, Memory<byte> destination, CancellationToken ct)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = await source.ReadAsync(destination[total..], ct).ConfigureAwait(false);
            if (read == 0)
                throw new CryptographicException("EncArrayBuffer header is truncated.");
            total += read;
        }
    }

    private TemporaryCiphertextLease OpenTemporaryCiphertext(ITransferIdleTimeoutPolicy timeoutPolicy)
    {
        var path = Path.Combine(_temporaryDirectory, $"winvaultwarden-{Guid.NewGuid():N}.ciphertext.tmp");
        Stream stream;
        try
        {
            stream = _temporaryStreamFactory(path)
                ?? throw new InvalidOperationException("The temporary stream factory returned no stream.");
        }
        catch
        {
            DeleteTemporaryFile(path);
            throw;
        }

        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
        {
            try
            {
                stream.Dispose();
            }
            finally
            {
                DeleteTemporaryFile(path);
            }
            throw new InvalidOperationException("The temporary ciphertext stream must support read, write, and seek.");
        }

        return new TemporaryCiphertextLease(stream, path, timeoutPolicy);
    }

    private static Stream CreateTemporaryCiphertextStream(string path) =>
        new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);

    private static void DeleteTemporaryFile(string? path)
    {
        if (path is null || !File.Exists(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateDeclaredLength(long? declaredLength, long maxEncryptedBytes)
    {
        if (declaredLength is not long declared)
            return;
        if (declared < EncArrayBuffer.MinimumEncodedLength)
            throw new InvalidDataException("Declared encrypted length is too short for EncArrayBuffer framing.");
        if (declared > maxEncryptedBytes)
            throw new InvalidDataException($"Declared encrypted length exceeds the {maxEncryptedBytes}-byte limit.");

        var ciphertextLength = declared - EncArrayBuffer.MetadataLength;
        if (ciphertextLength % EncArrayBuffer.CipherBlockLength != 0)
            throw new InvalidDataException("Declared EncArrayBuffer ciphertext length is not block aligned.");
    }

    private static void EnsureAuthenticatedKey(SymmetricCryptoKey key)
    {
        if (key.MacKey is null)
            throw new CryptographicException("EncArrayBuffer streaming requires a MAC key.");
    }

    private static async ValueTask<OpenedSourceLease> OpenSourceAsync(
        ReopenableFileSource source,
        ITransferIdleTimeoutPolicy timeoutPolicy,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var idleTimeout = timeoutPolicy.IdleTimeout;
        if (idleTimeout != Timeout.InfiniteTimeSpan && idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeoutPolicy));

        CancellationTokenSource? operationCancellation = new();
        Task<Stream> openTask;
        try
        {
            openTask = source.OpenReadAsync(operationCancellation.Token).AsTask();
        }
        catch
        {
            operationCancellation.Dispose();
            throw;
        }

        if (!openTask.IsCompleted)
        {
            var callerSignal = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var callerRegistration = ct.CanBeCanceled
                ? ct.UnsafeRegister(
                    static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null),
                    callerSignal)
                : default;
            using var timerCancellation = idleTimeout == Timeout.InfiniteTimeSpan
                ? null
                : new CancellationTokenSource();
            var timeoutTask = timerCancellation is null
                ? NeverSignal
                : Task.Delay(idleTimeout, timerCancellation.Token);
            var completed = await Task.WhenAny(openTask, callerSignal.Task, timeoutTask).ConfigureAwait(false);
            if (!ReferenceEquals(completed, openTask))
            {
                timerCancellation?.Cancel();
                var cancellationTask = operationCancellation.CancelAsync();
                _ = DisposeLateOpenResultAsync(openTask, cancellationTask, operationCancellation);
                operationCancellation = null;
                ct.ThrowIfCancellationRequested();
                throw new TransferIdleTimeoutException(TransferIoOperation.OpenSource, idleTimeout);
            }

            timerCancellation?.Cancel();
        }

        operationCancellation.Dispose();
        operationCancellation = null;
        if (ct.IsCancellationRequested)
        {
            if (openTask.Status == TaskStatus.RanToCompletion && openTask.Result is { } lateStream)
                await lateStream.DisposeAsync().ConfigureAwait(false);
            else if (openTask.IsFaulted)
                _ = openTask.Exception;
            ct.ThrowIfCancellationRequested();
        }

        var stream = await openTask.ConfigureAwait(false);
        if (stream is null)
            throw new InvalidDataException("The file source returned no stream.");
        if (!stream.CanRead)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new InvalidDataException("The file source returned a non-readable stream.");
        }

        var openedSource = new OpenedSourceLease(stream, timeoutPolicy);
        if (ct.IsCancellationRequested)
        {
            await openedSource.DisposeAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        return openedSource;
    }

    private static async Task DisposeLateOpenResultAsync(
        Task<Stream> openTask,
        Task cancellationTask,
        CancellationTokenSource operationCancellation)
    {
        try
        {
            var stream = await openTask.ConfigureAwait(false);
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await cancellationTask.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            try
            {
                operationCancellation.Dispose();
            }
            catch
            {
            }
        }
    }

    private sealed class OpenedSourceLease : IAsyncDisposable
    {
        private readonly Stream _inner;
        private int _disposed;

        public OpenedSourceLease(Stream inner, ITransferIdleTimeoutPolicy timeoutPolicy)
        {
            _inner = inner;
            Stream = new TransferIdleTimeoutStream(inner, timeoutPolicy);
        }

        public TransferIdleTimeoutStream Stream { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (Stream.HasPendingOperations)
            {
                _ = DisposeAfterPendingOperationsAsync(Stream, _inner);
                return;
            }

            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class TemporaryCiphertextLease : IAsyncDisposable
    {
        private readonly Stream _inner;
        private readonly string _path;
        private int _disposed;

        public TemporaryCiphertextLease(
            Stream inner,
            string path,
            ITransferIdleTimeoutPolicy timeoutPolicy)
        {
            _inner = inner;
            _path = path;
            Stream = new TransferIdleTimeoutStream(inner, timeoutPolicy);
        }

        public TransferIdleTimeoutStream Stream { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (Stream.HasPendingOperations)
            {
                _ = DisposeTemporaryAfterPendingOperationsAsync(Stream, _inner, _path);
                return;
            }

            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                DeleteTemporaryFile(_path);
            }
        }
    }

    private static async Task DisposeAfterPendingOperationsAsync(
        TransferIdleTimeoutStream timedStream,
        Stream inner)
    {
        try
        {
            await timedStream.WaitForPendingOperationsAsync().ConfigureAwait(false);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task DisposeTemporaryAfterPendingOperationsAsync(
        TransferIdleTimeoutStream timedStream,
        Stream inner,
        string path)
    {
        try
        {
            await timedStream.WaitForPendingOperationsAsync().ConfigureAwait(false);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            DeleteTemporaryFile(path);
        }
    }

    private static Aes CreateAes(SymmetricCryptoKey key, byte[] iv)
    {
        var aes = Aes.Create();
        try
        {
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key.EncKey;
            aes.IV = iv;
            return aes;
        }
        catch
        {
            aes.Dispose();
            throw;
        }
    }

    private sealed class IncrementalHashWriteStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class HmacTeeStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
