using System.Formats.Cbor;
using System.Security.Cryptography;

namespace Crypto.V2;

/// <summary>
/// Owns an Account Encryption V2 XAES-256-GCM user key and its 16-byte COSE key identifier.
/// </summary>
public sealed class V2UserKey : IDisposable
{
    public const int KeySize = Xaes256Gcm.KeySize;
    public const int KeyIdSize = 16;

    private const int SymmetricCoseKeyType = 4;
    private const int EncryptOperation = 3;
    private const int DecryptOperation = 4;
    private const int WrapKeyOperation = 5;
    private const int UnwrapKeyOperation = 6;

    private readonly byte[] _key;
    private readonly byte[] _keyId;
    private readonly int[] _operations;
    private int _disposed;

    private V2UserKey(byte[] key, byte[] keyId, int[] operations)
    {
        _key = key;
        _keyId = keyId;
        _operations = operations;
    }

    public string KeyIdHex
    {
        get
        {
            ThrowIfDisposed();
            return CosePrimitives.ToLowerHex(_keyId);
        }
    }

    public static V2UserKey Generate()
    {
        var key = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            var keyId = RandomNumberGenerator.GetBytes(KeyIdSize);
            return new V2UserKey(
                key,
                keyId,
                [EncryptOperation, DecryptOperation, WrapKeyOperation, UnwrapKeyOperation]);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    public static V2UserKey ImportBase64(string value)
    {
        var decoded = CosePrimitives.DecodeBase64Exact(value, nameof(value));
        try
        {
            return ImportTransport(decoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    public static V2UserKey ImportTransport(ReadOnlySpan<byte> paddedCoseKey)
    {
        if (paddedCoseKey.Length > CosePrimitives.MaximumEncodedObjectLength)
            throw new FormatException("V2 user key exceeds the protocol size limit.");
        var coseKey = CosePrimitives.UnpadTransportKey(paddedCoseKey);
        try
        {
            return ParseCoseKey(coseKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(coseKey);
        }
    }

    internal string ExportBase64()
    {
        var transport = ExportTransport();
        try
        {
            return Convert.ToBase64String(transport);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transport);
        }
    }

    public byte[] ExportTransport()
    {
        ThrowIfDisposed();
        var coseKey = EncodeCoseKey();
        try
        {
            return CosePrimitives.PadKeyForTransport(coseKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(coseKey);
        }
    }

    public string Encrypt(ReadOnlySpan<byte> plaintext, CoseContentFormat contentFormat)
    {
        ThrowIfDisposed();
        EnsureOperation(EncryptOperation);
        return CoseEncrypt0Codec.EncryptToEncString(plaintext, this, contentFormat);
    }

    public byte[] Decrypt(string type7EncString, CoseContentFormat expectedContentFormat)
    {
        ThrowIfDisposed();
        EnsureOperation(DecryptOperation);
        return CoseEncrypt0Codec.DecryptEncString(type7EncString, this, expectedContentFormat);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        CryptographicOperations.ZeroMemory(_key);
        CryptographicOperations.ZeroMemory(_keyId);
    }

    internal ReadOnlySpan<byte> KeyBytes
    {
        get
        {
            ThrowIfDisposed();
            return _key;
        }
    }

    internal ReadOnlySpan<byte> KeyIdBytes
    {
        get
        {
            ThrowIfDisposed();
            return _keyId;
        }
    }

    internal void ValidateEncryptOperation()
    {
        ThrowIfDisposed();
        EnsureOperation(EncryptOperation);
    }

    internal void ValidateDecryptOperation()
    {
        ThrowIfDisposed();
        EnsureOperation(DecryptOperation);
    }

    private static V2UserKey ParseCoseKey(ReadOnlyMemory<byte> encoded)
    {
        var reader = CosePrimitives.NewCanonicalReader(encoded);
        var mapLength = reader.ReadStartMap();
        if (mapLength != 5)
            throw new FormatException("A V2 user COSE key must contain exactly five parameters.");

        byte[]? key = null;
        byte[]? keyId = null;
        int[]? operations = null;
        long? algorithm = null;
        int? keyType = null;
        var seen = new HashSet<long>();

        try
        {
            for (var index = 0; index < mapLength.Value; index++)
            {
                var label = reader.ReadInt64();
                if (!seen.Add(label))
                    throw new FormatException("COSE key contains a duplicate parameter.");

                switch (label)
                {
                    case 1:
                        keyType = checked((int)reader.ReadInt64());
                        break;
                    case 2:
                        keyId = reader.ReadByteString();
                        break;
                    case 3:
                        algorithm = reader.ReadInt64();
                        break;
                    case 4:
                        operations = ReadOperations(reader);
                        break;
                    case -1:
                        key = reader.ReadByteString();
                        break;
                    default:
                        throw new FormatException($"Unsupported V2 user COSE key parameter: {label}.");
                }
            }

            reader.ReadEndMap();
            CosePrimitives.EnsureFinished(reader);

            if (keyType != SymmetricCoseKeyType
                || algorithm != CosePrimitives.Xaes256GcmAlgorithm
                || key is not { Length: KeySize }
                || keyId is not { Length: KeyIdSize }
                || operations is null)
            {
                throw new FormatException("COSE key is not a valid Bitwarden XAES-256-GCM user key.");
            }

            var result = new V2UserKey(key, keyId, operations);
            key = null;
            keyId = null;
            return result;
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
            if (keyId is not null)
                CryptographicOperations.ZeroMemory(keyId);
        }
    }

    private byte[] EncodeCoseKey()
    {
        var writer = CosePrimitives.NewCanonicalWriter();
        writer.WriteStartMap(5);
        writer.WriteInt32(1);
        writer.WriteInt32(SymmetricCoseKeyType);
        writer.WriteInt32(2);
        writer.WriteByteString(_keyId);
        writer.WriteInt32(3);
        writer.WriteInt64(CosePrimitives.Xaes256GcmAlgorithm);
        writer.WriteInt32(4);
        writer.WriteStartArray(_operations.Length);
        foreach (var operation in _operations)
            writer.WriteInt32(operation);
        writer.WriteEndArray();
        writer.WriteInt32(-1);
        writer.WriteByteString(_key);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private static int[] ReadOperations(CborReader reader)
    {
        var length = reader.ReadStartArray();
        if (length is null or < 1 or > 4)
            throw new FormatException("COSE key operations must be a definite non-empty array.");

        var operations = new int[length.Value];
        var seen = new HashSet<int>();
        for (var index = 0; index < operations.Length; index++)
        {
            var operation = checked((int)reader.ReadInt64());
            if (operation is not (EncryptOperation or DecryptOperation or WrapKeyOperation or UnwrapKeyOperation)
                || !seen.Add(operation))
            {
                throw new FormatException("COSE key contains an unsupported or duplicate operation.");
            }
            operations[index] = operation;
        }
        reader.ReadEndArray();
        Array.Sort(operations);
        return operations;
    }

    private void EnsureOperation(int operation)
    {
        if (Array.BinarySearch(_operations, operation) < 0)
            throw new CryptographicException("The COSE key does not permit the requested operation.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
