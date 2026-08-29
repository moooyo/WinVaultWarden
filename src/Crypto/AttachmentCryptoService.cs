using System.Security.Cryptography;
using System.Text;
using Core.Models;
using Core.Services;

namespace Crypto;

// Bitwarden attachment encryption. Each modern attachment has an independent 64-byte key used
// for its name and body; the item key wraps that attachment key for the DTO. Callers pass the item
// key directly for legacy attachments that have no wrapped attachment key.
public sealed class AttachmentCryptoService
{
    private const int AttachmentKeyLength = 64;

    private readonly CryptoService _crypto;
    private readonly EncArrayBufferStreamCrypto _streamCrypto;

    public AttachmentCryptoService(CryptoService crypto, EncArrayBufferStreamCrypto? streamCrypto = null)
    {
        _crypto = crypto;
        _streamCrypto = streamCrypto ?? new EncArrayBufferStreamCrypto();
    }

    public static long MaxEncryptedFileBytes { get; } =
        EncArrayBuffer.GetEncodedLength(AttachmentLimits.MaxPlaintextBytes);

    // The first 32 bytes encrypt and the final 32 bytes authenticate.
    public SymmetricCryptoKey GenerateAttachmentKey()
        => new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(AttachmentKeyLength));

    public string WrapKey(SymmetricCryptoKey attachmentKey, SymmetricCryptoKey itemKey)
        => _crypto.Encrypt(attachmentKey.FullKey, itemKey).ToString();

    public SymmetricCryptoKey UnwrapKey(string wrapped, SymmetricCryptoKey itemKey)
        => new SymmetricCryptoKey(_crypto.Decrypt(EncString.Parse(wrapped), itemKey));

    public string EncryptFileName(string fileName, SymmetricCryptoKey key)
        => _crypto.Encrypt(Encoding.UTF8.GetBytes(fileName), key).ToString();

    public string? DecryptFileName(string? enc, SymmetricCryptoKey key)
    {
        if (string.IsNullOrEmpty(enc))
            return null;
        return Encoding.UTF8.GetString(_crypto.Decrypt(EncString.Parse(enc), key));
    }

    public byte[] EncryptFile(byte[] plaintext, SymmetricCryptoKey key)
        => EncArrayBuffer.Pack(_crypto.Encrypt(plaintext, key));

    public byte[] DecryptFile(byte[] buffer, SymmetricCryptoKey key)
        => _crypto.Decrypt(EncArrayBuffer.Unpack(buffer), key);

    public Task<StreamUploadContent> PrepareFileUploadAsync(
        ReopenableFileSource source,
        SymmetricCryptoKey key,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.FileName);
        return _streamCrypto.PrepareUploadAsync(
            source,
            key,
            AttachmentLimits.MaxPlaintextBytes,
            ct);
    }

    public Task<StreamUploadContent> PrepareFileUploadAsync(
        ReopenableFileSource source,
        SymmetricCryptoKey key,
        ITransferIdleTimeoutPolicy timeoutPolicy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeoutPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.FileName);
        return _streamCrypto.PrepareUploadAsync(
            source,
            key,
            AttachmentLimits.MaxPlaintextBytes,
            timeoutPolicy,
            ct);
    }

    public Task<long> DecryptFileAsync(
        Stream encryptedSource,
        Stream plaintextDestination,
        SymmetricCryptoKey key,
        long? declaredLength,
        CancellationToken ct = default) =>
        _streamCrypto.DecryptAsync(
            encryptedSource,
            plaintextDestination,
            key,
            MaxEncryptedFileBytes,
            AttachmentLimits.MaxPlaintextBytes,
            declaredLength,
            ct);

    public Task<long> DecryptFileAsync(
        Stream encryptedSource,
        Stream plaintextDestination,
        SymmetricCryptoKey key,
        long? declaredLength,
        ITransferIdleTimeoutPolicy timeoutPolicy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(timeoutPolicy);
        return _streamCrypto.DecryptAsync(
            encryptedSource,
            plaintextDestination,
            key,
            MaxEncryptedFileBytes,
            AttachmentLimits.MaxPlaintextBytes,
            declaredLength,
            timeoutPolicy,
            ct);
    }
}
