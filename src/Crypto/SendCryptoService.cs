using System.Security.Cryptography;
using System.Text;
using Core.Models;
using Core.Services;

namespace Crypto;

// Bitwarden Send encryption aligned with bitwarden/clients.
// seed(16B) --HKDF(bitwarden-send/send,64B)--> cryptoKey(enc32+mac32)
// Fields use cryptoKey; userKey wraps the seed stored in the key field.
public sealed class SendCryptoService
{
    private const int SeedLength = 16;
    private const int EncodedSeedLength = 22;
    private const string ShareFragmentPrefix = "#/send/";
    private const int SendKdfIterations = 100_000;
    private static readonly byte[] HkdfSalt = "bitwarden-send"u8.ToArray();
    private static readonly byte[] HkdfInfo = "send"u8.ToArray();
    private const string ShareUrlMarker = "/#/send/";

    public const int MaxShareUrlLength = 2_048;
    public const int MaxShareFragmentLength = 158;
    public const int MaxAccessIdLength = 128;

    private readonly CryptoService _crypto;
    private readonly EncArrayBufferStreamCrypto _streamCrypto;

    public SendCryptoService(CryptoService crypto, EncArrayBufferStreamCrypto? streamCrypto = null)
    {
        _crypto = crypto;
        _streamCrypto = streamCrypto ?? new EncArrayBufferStreamCrypto();
    }

    public static long MaxEncryptedFileBytes { get; } =
        EncArrayBuffer.GetEncodedLength(SendFileSource.MaxPlaintextBytes);

    // The random 16-byte seed is placed in the share URL fragment and wrapped for the key field.
    public byte[] GenerateSeed() => RandomNumberGenerator.GetBytes(SeedLength);

    // Full HKDF extract-and-expand with SHA-256, producing enc(32) and MAC(32) keys.
    public SymmetricCryptoKey DeriveCryptoKey(byte[] seed)
    {
        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: seed,
            outputLength: 64,
            salt: HkdfSalt,
            info: HkdfInfo);
        return new SymmetricCryptoKey(key);
    }

    // The key field is a type-2 EncString containing the seed encrypted by userKey.
    public string WrapSeed(byte[] seed, SymmetricCryptoKey userKey)
        => _crypto.Encrypt(seed, userKey).ToString();

    public byte[] UnwrapSeed(string encKey, SymmetricCryptoKey userKey)
        => _crypto.Decrypt(EncString.Parse(encKey), userKey);

    // Send text fields are UTF-8 encoded and encrypted with cryptoKey.
    public string EncryptField(string plaintext, SymmetricCryptoKey cryptoKey)
        => _crypto.Encrypt(Encoding.UTF8.GetBytes(plaintext), cryptoKey).ToString();

    public string? DecryptField(string? encText, SymmetricCryptoKey cryptoKey)
    {
        if (string.IsNullOrEmpty(encText))
            return null;
        var bytes = _crypto.Decrypt(EncString.Parse(encText), cryptoKey);
        return Encoding.UTF8.GetString(bytes);
    }

    // File body layout is type(2), IV(16), MAC(32), and ciphertext.
    public byte[] EncryptToBuffer(byte[] plaintext, SymmetricCryptoKey cryptoKey)
        => EncArrayBuffer.Pack(_crypto.Encrypt(plaintext, cryptoKey));

    // This array API remains for small compatibility callers; file transfers use the stream API.
    public byte[] DecryptBuffer(byte[] buffer, SymmetricCryptoKey cryptoKey)
        => _crypto.Decrypt(EncArrayBuffer.Unpack(buffer), cryptoKey);

    // Retains the existing Send contract while delegating both passes to the shared file crypto.
    public Task<StreamUploadContent> PrepareFileUploadAsync(
        SendFileSource source,
        SymmetricCryptoKey cryptoKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.FileName);
        return _streamCrypto.PrepareUploadAsync(
            source,
            cryptoKey,
            SendFileSource.MaxPlaintextBytes,
            ct);
    }

    public Task<StreamUploadContent> PrepareFileUploadAsync(
        SendFileSource source,
        SymmetricCryptoKey cryptoKey,
        ITransferIdleTimeoutPolicy timeoutPolicy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeoutPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.FileName);
        return _streamCrypto.PrepareUploadAsync(
            source,
            cryptoKey,
            SendFileSource.MaxPlaintextBytes,
            timeoutPolicy,
            ct);
    }

    public Task<long> DecryptFileAsync(
        Stream encryptedSource,
        Stream plaintextDestination,
        SymmetricCryptoKey cryptoKey,
        long? declaredLength,
        CancellationToken ct = default) =>
        _streamCrypto.DecryptAsync(
            encryptedSource,
            plaintextDestination,
            cryptoKey,
            MaxEncryptedFileBytes,
            SendFileSource.MaxPlaintextBytes,
            declaredLength,
            ct);

    public Task<long> DecryptFileAsync(
        Stream encryptedSource,
        Stream plaintextDestination,
        SymmetricCryptoKey cryptoKey,
        long? declaredLength,
        ITransferIdleTimeoutPolicy timeoutPolicy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(timeoutPolicy);
        return _streamCrypto.DecryptAsync(
            encryptedSource,
            plaintextDestination,
            cryptoKey,
            MaxEncryptedFileBytes,
            SendFileSource.MaxPlaintextBytes,
            declaredLength,
            timeoutPolicy,
            ct);
    }

    // Password proof is base64(PBKDF2-SHA256(password, seed, 100000, 32B)).
    public string ComputePasswordProof(string password, byte[] seed)
    {
        var proof = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            seed,
            SendKdfIterations,
            HashAlgorithmName.SHA256,
            32);
        return Convert.ToBase64String(proof);
    }

    // Share URL uses the access id and unpadded base64url seed in the fragment.
    public string BuildShareUrl(string serverUrl, string accessId, byte[] seed)
        => $"{serverUrl.TrimEnd('/')}{ShareUrlMarker}{accessId}/{Base64UrlEncode(seed)}";

    /// <summary>
    /// Parses and validates one bounded Send share URL before decoding its fixed-size seed.
    /// </summary>
    public bool TryParseShareUrl(string? url, out ParsedSendShareUrl? result)
    {
        result = null;
        if (url is null
            || url.Length == 0
            || url.Length > MaxShareUrlLength
            || ContainsWhitespace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var shareUri)
            || (!shareUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !shareUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(shareUri.Host)
            || !string.IsNullOrEmpty(shareUri.UserInfo)
            || !string.IsNullOrEmpty(shareUri.Query))
            return false;

        var fragment = shareUri.Fragment;
        if (fragment.Length > MaxShareFragmentLength
            || !fragment.StartsWith(ShareFragmentPrefix, StringComparison.Ordinal))
            return false;

        var separator = fragment.IndexOf('/', ShareFragmentPrefix.Length);
        if (separator < 0 || fragment.IndexOf('/', separator + 1) >= 0)
            return false;

        var accessIdLength = separator - ShareFragmentPrefix.Length;
        var encodedSeedLength = fragment.Length - separator - 1;
        if (accessIdLength <= 0
            || accessIdLength > MaxAccessIdLength
            || encodedSeedLength != EncodedSeedLength)
            return false;

        var accessIdSpan = fragment.AsSpan(ShareFragmentPrefix.Length, accessIdLength);
        var encodedSeed = fragment.AsSpan(separator + 1, encodedSeedLength);
        if (!IsBase64Url(accessIdSpan) || !IsBase64Url(encodedSeed))
            return false;

        ServerEndpoints endpoints;
        try
        {
            var webVault = new ServerEndpoint(shareUri.GetLeftPart(UriPartial.Path));
            endpoints = ServerEndpoints.FromWebVault(webVault);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!TryDecodeSeed(encodedSeed, out var seed))
            return false;

        try
        {
            result = new ParsedSendShareUrl(
                shareUri,
                accessIdSpan.ToString(),
                endpoints,
                seed);
            return true;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(seed);
            throw;
        }
    }

    // Compatibility overload. New callers should consume the single validated parse result.
    public bool TryParseShareUrl(string url, out string accessId, out byte[] seed)
    {
        accessId = string.Empty;
        seed = Array.Empty<byte>();
        if (!TryParseShareUrl(url, out var parsed) || parsed is null)
            return false;

        using (parsed)
        {
            accessId = parsed.AccessId;
            seed = parsed.CopySeed();
            return true;
        }
    }

    // Unpadded base64url encoding.
    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static bool TryDecodeSeed(ReadOnlySpan<char> encoded, out byte[] seed)
    {
        seed = Array.Empty<byte>();
        if (encoded.Length != EncodedSeedLength)
            return false;

        Span<char> padded = stackalloc char[24];
        for (var index = 0; index < encoded.Length; index++)
        {
            padded[index] = encoded[index] switch
            {
                '-' => '+',
                '_' => '/',
                var character => character,
            };
        }
        padded[22] = '=';
        padded[23] = '=';

        var decoded = new byte[SeedLength];
        if (!Convert.TryFromBase64Chars(padded, decoded, out var written)
            || written != SeedLength)
        {
            CryptographicOperations.ZeroMemory(decoded);
            return false;
        }

        seed = decoded;
        return true;
    }

    private static bool IsBase64Url(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character != '-'
                && character != '_')
                return false;
        }

        return true;
    }

    private static bool ContainsWhitespace(string value)
    {
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
                return true;
        }

        return false;
    }
}
