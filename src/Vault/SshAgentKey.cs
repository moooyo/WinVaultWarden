using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Vault;

/// <summary>
/// An in-memory SSH signing key. No serialized private key is retained after parsing.
/// RSA and ECDSA private material is owned by a disposable platform provider; the
/// Ed25519 seed is explicitly cleared on disposal. Returned signatures use SSH wire encoding.
/// </summary>
public sealed class SshAgentKey : IDisposable
{
    public const int MaxPrivateKeyCharacters = 65_536;
    public const int MaxSignedDataBytes = 1_048_576;
    public const uint RsaSha256Flag = 2;
    public const uint RsaSha512Flag = 4;

    private readonly object _gate = new();
    private readonly byte[] _publicKeyBlob;
    private RSA? _rsa;
    private ECDsa? _ecdsa;
    private byte[]? _ed25519Seed;
    private bool _disposed;

    private SshAgentKey(string algorithm, byte[] publicKeyBlob,
        RSA? rsa = null, ECDsa? ecdsa = null, byte[]? ed25519Seed = null)
    {
        Algorithm = algorithm;
        _publicKeyBlob = publicKeyBlob;
        _rsa = rsa;
        _ecdsa = ecdsa;
        _ed25519Seed = ed25519Seed;
        Fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKeyBlob)).TrimEnd('=');
    }

    public string Algorithm { get; }
    public string Fingerprint { get; }
    public byte[] PublicKeyBlob => _publicKeyBlob.ToArray();

    /// <summary>Parses one unencrypted OpenSSH, PKCS#8, PKCS#1, or SEC1 PEM key.</summary>
    public static SshAgentKey Parse(string privateKey, string? expectedPublicKey = null)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        if (privateKey.Length is 0 or > MaxPrivateKeyCharacters)
            throw new CryptographicException("The SSH private key exceeds the supported size.");

        byte[]? decoded = null;
        SshAgentKey? key = null;
        try
        {
            if (!PemEncoding.TryFind(privateKey, out var fields))
                throw new CryptographicException("A single unencrypted PEM key is required.");
            var (offset, length) = fields.Location.GetOffsetAndLength(privateKey.Length);
            if (!IsWhiteSpace(privateKey.AsSpan(0, offset))
                || !IsWhiteSpace(privateKey.AsSpan(offset + length)))
                throw new CryptographicException("Multiple keys and trailing key data are not accepted.");

            var label = privateKey[fields.Label];
            decoded = Convert.FromBase64String(privateKey[fields.Base64Data]);
            key = label switch
            {
                "OPENSSH PRIVATE KEY" => ParseOpenSsh(decoded),
                "RSA PRIVATE KEY" => ParseRsa(decoded, pkcs8: false),
                "EC PRIVATE KEY" => ParseEc(decoded, pkcs8: false),
                "PRIVATE KEY" => ParsePkcs8(decoded),
                _ => throw new CryptographicException("This SSH private-key format is not supported."),
            };
            if (!string.IsNullOrWhiteSpace(expectedPublicKey))
                key.ValidateExpectedPublicKey(expectedPublicKey);
            return key;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            key?.Dispose();
            if (exception is CryptographicException)
                throw;
            throw new CryptographicException("The SSH private key is invalid or unsupported.", exception);
        }
        finally
        {
            if (decoded is not null)
                CryptographicOperations.ZeroMemory(decoded);
        }
    }

    /// <summary>
    /// Produces an SSH signature blob. RSA/SHA-1 and unrecognized flags are rejected.
    /// Callers must obtain fresh user approval before invoking this method.
    /// </summary>
    public byte[] Sign(ReadOnlySpan<byte> data, uint flags)
    {
        if (data.Length > MaxSignedDataBytes)
            throw new CryptographicException("The SSH signature input exceeds the supported size.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            byte[] signature;
            string signatureAlgorithm;
            if (_rsa is not null)
            {
                var hash = flags switch
                {
                    RsaSha256Flag => HashAlgorithmName.SHA256,
                    RsaSha512Flag => HashAlgorithmName.SHA512,
                    _ => throw new CryptographicException("RSA requires exactly one supported SHA-2 flag."),
                };
                signatureAlgorithm = flags == RsaSha256Flag ? "rsa-sha2-256" : "rsa-sha2-512";
                signature = _rsa.SignData(data, hash, RSASignaturePadding.Pkcs1);
            }
            else
            {
                if (flags != 0)
                    throw new CryptographicException("This SSH key does not accept signature flags.");
                signatureAlgorithm = Algorithm;
                if (_ecdsa is not null)
                {
                    var hash = Algorithm switch
                    {
                        "ecdsa-sha2-nistp256" => HashAlgorithmName.SHA256,
                        "ecdsa-sha2-nistp384" => HashAlgorithmName.SHA384,
                        "ecdsa-sha2-nistp521" => HashAlgorithmName.SHA512,
                        _ => throw new CryptographicException("The SSH elliptic curve is unsupported."),
                    };
                    var raw = _ecdsa.SignData(data, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                    try
                    {
                        using var encoded = new MemoryStream();
                        WriteMpint(encoded, raw.AsSpan(0, raw.Length / 2));
                        WriteMpint(encoded, raw.AsSpan(raw.Length / 2));
                        signature = encoded.ToArray();
                    }
                    finally { CryptographicOperations.ZeroMemory(raw); }
                }
                else
                {
                    signature = new byte[Ed25519.SignatureSize];
                    Ed25519.Sign(_ed25519Seed!, data, signature);
                }
            }
            try
            {
                using var result = new MemoryStream();
                WriteString(result, Encoding.ASCII.GetBytes(signatureAlgorithm));
                WriteString(result, signature);
                return result.ToArray();
            }
            finally { CryptographicOperations.ZeroMemory(signature); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _rsa?.Dispose();
            _ecdsa?.Dispose();
            if (_ed25519Seed is not null) CryptographicOperations.ZeroMemory(_ed25519Seed);
            _rsa = null;
            _ecdsa = null;
            _ed25519Seed = null;
        }
    }

    private static SshAgentKey ParsePkcs8(byte[] encoded)
    {
        var reader = new AsnReader(encoded, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        var version = sequence.ReadInteger();
        var algorithm = sequence.ReadSequence();
        var oid = algorithm.ReadObjectIdentifier();
        return oid switch
        {
            "1.2.840.113549.1.1.1" => ParseRsa(encoded, pkcs8: true),
            "1.2.840.10045.2.1" => ParseEc(encoded, pkcs8: true),
            "1.3.101.112" => ParseEd25519(reader, sequence, algorithm, version),
            _ => throw new CryptographicException("The SSH key algorithm is unsupported."),
        };
    }

    private static SshAgentKey ParseRsa(byte[] encoded, bool pkcs8)
    {
        var rsa = RSA.Create();
        try
        {
            int read;
            if (pkcs8) rsa.ImportPkcs8PrivateKey(encoded, out read);
            else rsa.ImportRSAPrivateKey(encoded, out read);
            if (read != encoded.Length || rsa.KeySize is < 2048 or > 8192)
                throw new CryptographicException("RSA SSH keys must be between 2048 and 8192 bits.");
            return FromRsa(rsa);
        }
        catch { rsa.Dispose(); throw; }
    }

    private static SshAgentKey FromRsa(RSA rsa)
    {
        var proof = rsa.SignData("SSH private-key consistency check"u8, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        try
        {
            if (!rsa.VerifyData("SSH private-key consistency check"u8, proof, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new CryptographicException("The RSA public and private parameters do not match.");
        }
        finally { CryptographicOperations.ZeroMemory(proof); }
        var parameters = rsa.ExportParameters(false);
        using var publicBlob = new MemoryStream();
        WriteString(publicBlob, "ssh-rsa"u8);
        WriteMpint(publicBlob, parameters.Exponent!);
        WriteMpint(publicBlob, parameters.Modulus!);
        return new SshAgentKey("ssh-rsa", publicBlob.ToArray(), rsa: rsa);
    }

    private static SshAgentKey ParseEc(byte[] encoded, bool pkcs8)
    {
        var ec = ECDsa.Create();
        try
        {
            int read;
            if (pkcs8) ec.ImportPkcs8PrivateKey(encoded, out read);
            else ec.ImportECPrivateKey(encoded, out read);
            if (read != encoded.Length)
                throw new CryptographicException("Trailing EC key data is not accepted.");
            return FromEc(ec);
        }
        catch { ec.Dispose(); throw; }
    }

    private static SshAgentKey FromEc(ECDsa ec)
    {
        var proof = ec.SignData("SSH private-key consistency check"u8, HashAlgorithmName.SHA256);
        try
        {
            if (!ec.VerifyData("SSH private-key consistency check"u8, proof, HashAlgorithmName.SHA256))
                throw new CryptographicException("The EC public and private parameters do not match.");
        }
        finally { CryptographicOperations.ZeroMemory(proof); }
        var parameters = ec.ExportParameters(false);
        var name = parameters.Curve.Oid.Value switch
        {
            "1.2.840.10045.3.1.7" => "nistp256",
            "1.3.132.0.34" => "nistp384",
            "1.3.132.0.35" => "nistp521",
            _ => throw new CryptographicException("Only the NIST P-256, P-384 and P-521 SSH curves are supported."),
        };
        var point = new byte[1 + parameters.Q.X!.Length + parameters.Q.Y!.Length];
        point[0] = 4;
        parameters.Q.X.CopyTo(point, 1);
        parameters.Q.Y.CopyTo(point, 1 + parameters.Q.X.Length);
        using var blob = new MemoryStream();
        WriteString(blob, Encoding.ASCII.GetBytes("ecdsa-sha2-" + name));
        WriteString(blob, Encoding.ASCII.GetBytes(name));
        WriteString(blob, point);
        return new SshAgentKey("ecdsa-sha2-" + name, blob.ToArray(), ecdsa: ec);
    }

    private static SshAgentKey ParseEd25519(
        AsnReader outer, AsnReader sequence, AsnReader algorithm, System.Numerics.BigInteger version)
    {
        if (algorithm.HasData || version < 0 || version > 1)
            throw new CryptographicException("Ed25519 parameters must be absent.");
        var wrapped = sequence.ReadOctetString();
        byte[]? seed = null;
        SshAgentKey? result = null;
        try
        {
            var inner = new AsnReader(wrapped, AsnEncodingRules.DER);
            seed = inner.ReadOctetString();
            inner.ThrowIfNotEmpty();
            if (seed.Length != Ed25519.SecretKeySize)
                throw new CryptographicException("An Ed25519 seed must contain exactly 32 bytes.");
            result = FromEd25519(seed);
            var attributesTag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
            if (sequence.HasData && sequence.PeekTag().HasSameClassAndValue(attributesTag))
                sequence.ReadEncodedValue();
            if (sequence.HasData)
            {
                var expected = sequence.ReadBitString(out var unusedBits, new Asn1Tag(TagClass.ContextSpecific, 1));
                var derived = new byte[Ed25519.PublicKeySize];
                Ed25519.GeneratePublicKey(seed, derived);
                if (version != 1 || unusedBits != 0 || !expected.AsSpan().SequenceEqual(derived))
                    throw new CryptographicException("The Ed25519 public key does not match the private seed.");
            }
            sequence.ThrowIfNotEmpty();
            outer.ThrowIfNotEmpty();
            return result;
        }
        catch { result?.Dispose(); throw; }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapped);
            if (seed is not null) CryptographicOperations.ZeroMemory(seed);
        }
    }

    private static SshAgentKey FromEd25519(ReadOnlySpan<byte> seed)
    {
        var publicKey = new byte[Ed25519.PublicKeySize];
        Ed25519.GeneratePublicKey(seed, publicKey);
        using var blob = new MemoryStream();
        WriteString(blob, "ssh-ed25519"u8);
        WriteString(blob, publicKey);
        return new SshAgentKey("ssh-ed25519", blob.ToArray(), ed25519Seed: seed.ToArray());
    }

    private static SshAgentKey ParseOpenSsh(ReadOnlySpan<byte> encoded)
    {
        ReadOnlySpan<byte> magic = "openssh-key-v1\0"u8;
        if (!encoded.StartsWith(magic)) throw new CryptographicException("The OpenSSH key header is invalid.");
        var reader = new KeyReader(encoded[magic.Length..]);
        if (!reader.ReadString().SequenceEqual("none"u8)
            || !reader.ReadString().SequenceEqual("none"u8)
            || !reader.ReadString().IsEmpty)
            throw new CryptographicException("Encrypted OpenSSH keys are not supported.");
        if (reader.ReadUInt32() != 1)
            throw new CryptographicException("Exactly one OpenSSH key is required.");
        var advertisedPublicKey = reader.ReadString();
        var privateBlock = reader.ReadString();
        if (!reader.IsEmpty || privateBlock.IsEmpty || privateBlock.Length % 8 != 0)
            throw new CryptographicException("The OpenSSH private-key block is malformed.");
        var inner = new KeyReader(privateBlock);
        if (inner.ReadUInt32() != inner.ReadUInt32())
            throw new CryptographicException("The OpenSSH key check values differ.");
        var algorithm = Encoding.ASCII.GetString(inner.ReadString());
        SshAgentKey? result = null;
        try
        {
            result = algorithm switch
            {
                "ssh-ed25519" => ParseOpenSshEd25519(ref inner),
                "ssh-rsa" => ParseOpenSshRsa(ref inner),
                "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521"
                    => ParseOpenSshEc(algorithm, ref inner),
                _ => throw new CryptographicException("The OpenSSH key algorithm is unsupported."),
            };
            if (!advertisedPublicKey.SequenceEqual(result._publicKeyBlob))
                throw new CryptographicException("The OpenSSH public and private keys do not match.");
            if (inner.ReadString().Length > 4096)
                throw new CryptographicException("The OpenSSH key comment is too large.");
            var padding = inner.Remaining;
            if (padding.Length > 8)
                throw new CryptographicException("The OpenSSH key padding is invalid.");
            for (var index = 0; index < padding.Length; index++)
                if (padding[index] != index + 1)
                    throw new CryptographicException("The OpenSSH key padding is invalid.");
            return result;
        }
        catch { result?.Dispose(); throw; }
    }

    private static SshAgentKey ParseOpenSshEd25519(ref KeyReader reader)
    {
        var publicKey = reader.ReadString();
        var privateKey = reader.ReadString();
        if (publicKey.Length != 32 || privateKey.Length != 64 || !publicKey.SequenceEqual(privateKey[32..]))
            throw new CryptographicException("The OpenSSH Ed25519 key is malformed.");
        var derived = new byte[32];
        Ed25519.GeneratePublicKey(privateKey[..32], derived);
        if (!derived.AsSpan().SequenceEqual(publicKey))
            throw new CryptographicException("The OpenSSH Ed25519 public key does not match the seed.");
        return FromEd25519(privateKey[..32]);
    }

    private static SshAgentKey ParseOpenSshRsa(ref KeyReader reader)
    {
        var n = reader.ReadPositiveMpint();
        var e = reader.ReadPositiveMpint();
        var d = reader.ReadPositiveMpint();
        var iqmp = reader.ReadPositiveMpint();
        var p = reader.ReadPositiveMpint();
        var q = reader.ReadPositiveMpint();
        if (n.BitLength is < 2048 or > 8192 || !p.Multiply(q).Equals(n)
            || !q.ModInverse(p).Equals(iqmp)
            || !d.Multiply(e).Mod(p.Subtract(BigInteger.One)).Equals(BigInteger.One)
            || !d.Multiply(e).Mod(q.Subtract(BigInteger.One)).Equals(BigInteger.One))
            throw new CryptographicException("The OpenSSH RSA key parameters are inconsistent.");
        var size = (n.BitLength + 7) / 8;
        var primeSize = (size + 1) / 2;
        var parameters = new RSAParameters
        {
            Modulus = n.ToByteArrayUnsigned(), Exponent = e.ToByteArrayUnsigned(),
            D = Padded(d, size), P = Padded(p, primeSize), Q = Padded(q, primeSize),
            DP = Padded(d.Mod(p.Subtract(BigInteger.One)), primeSize),
            DQ = Padded(d.Mod(q.Subtract(BigInteger.One)), primeSize),
            InverseQ = Padded(iqmp, primeSize),
        };
        var rsa = RSA.Create();
        try
        {
            rsa.ImportParameters(parameters);
            return FromRsa(rsa);
        }
        catch { rsa.Dispose(); throw; }
        finally
        {
            foreach (var value in new[] { parameters.D, parameters.P, parameters.Q, parameters.DP, parameters.DQ, parameters.InverseQ })
                if (value is not null) CryptographicOperations.ZeroMemory(value);
        }
    }

    private static SshAgentKey ParseOpenSshEc(string algorithm, ref KeyReader reader)
    {
        var curveName = Encoding.ASCII.GetString(reader.ReadString());
        if (algorithm != "ecdsa-sha2-" + curveName)
            throw new CryptographicException("The OpenSSH EC algorithm and curve disagree.");
        var (oid, curve, size) = curveName switch
        {
            "nistp256" => ("1.2.840.10045.3.1.7", ECCurve.NamedCurves.nistP256, 32),
            "nistp384" => ("1.3.132.0.34", ECCurve.NamedCurves.nistP384, 48),
            "nistp521" => ("1.3.132.0.35", ECCurve.NamedCurves.nistP521, 66),
            _ => throw new CryptographicException("The OpenSSH elliptic curve is unsupported."),
        };
        var point = reader.ReadString();
        var scalar = reader.ReadPositiveMpint();
        var domain = Org.BouncyCastle.Asn1.Sec.SecNamedCurves.GetByOid(new Org.BouncyCastle.Asn1.DerObjectIdentifier(oid));
        if (scalar.SignValue <= 0 || scalar.CompareTo(domain.N) >= 0)
            throw new CryptographicException("The OpenSSH EC private scalar is invalid.");
        var derived = domain.G.Multiply(scalar).Normalize().GetEncoded(false);
        if (!point.SequenceEqual(derived))
            throw new CryptographicException("The OpenSSH EC public key does not match its private scalar.");
        var secret = Padded(scalar, size);
        var ec = ECDsa.Create();
        try
        {
            ec.ImportParameters(new ECParameters
            {
                Curve = curve, D = secret,
                Q = new ECPoint { X = derived.AsSpan(1, size).ToArray(), Y = derived.AsSpan(1 + size, size).ToArray() },
            });
            return FromEc(ec);
        }
        catch { ec.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    private void ValidateExpectedPublicKey(string value)
    {
        if (value.Length > MaxPrivateKeyCharacters)
            throw new CryptographicException("The SSH public key exceeds the supported size.");
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != Algorithm)
            throw new CryptographicException("The stored SSH public key does not match the private key.");
        var expected = Convert.FromBase64String(parts[1]);
        if (!expected.AsSpan().SequenceEqual(_publicKeyBlob))
            throw new CryptographicException("The stored SSH public key does not match the private key.");
    }

    private static bool IsWhiteSpace(ReadOnlySpan<char> value)
    {
        foreach (var character in value) if (!char.IsWhiteSpace(character)) return false;
        return true;
    }

    private static byte[] Padded(BigInteger value, int size)
    {
        var encoded = value.ToByteArrayUnsigned();
        try
        {
            if (encoded.Length > size) throw new CryptographicException("An SSH key integer is oversized.");
            var result = new byte[size];
            encoded.CopyTo(result, size - encoded.Length);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    private static void WriteString(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> prefix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)value.Length));
        stream.Write(prefix);
        stream.Write(value);
    }

    private static void WriteMpint(Stream stream, ReadOnlySpan<byte> value)
    {
        while (!value.IsEmpty && value[0] == 0) value = value[1..];
        var needsZero = !value.IsEmpty && (value[0] & 0x80) != 0;
        Span<byte> prefix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)value.Length + (needsZero ? 1u : 0u)));
        stream.Write(prefix);
        if (needsZero) stream.WriteByte(0);
        stream.Write(value);
    }

    private ref struct KeyReader(ReadOnlySpan<byte> remaining)
    {
        private ReadOnlySpan<byte> _remaining = remaining;
        public readonly bool IsEmpty => _remaining.IsEmpty;
        public readonly ReadOnlySpan<byte> Remaining => _remaining;
        public uint ReadUInt32()
        {
            if (_remaining.Length < 4) throw new CryptographicException("Truncated SSH key integer.");
            var result = BinaryPrimitives.ReadUInt32BigEndian(_remaining);
            _remaining = _remaining[4..];
            return result;
        }
        public ReadOnlySpan<byte> ReadString()
        {
            var length = ReadUInt32();
            if (length > _remaining.Length) throw new CryptographicException("Truncated SSH key field.");
            var result = _remaining[..(int)length];
            _remaining = _remaining[(int)length..];
            return result;
        }
        public BigInteger ReadPositiveMpint()
        {
            var value = ReadString();
            if (value.IsEmpty || (value[0] & 0x80) != 0
                || value.Length > 1 && value[0] == 0 && (value[1] & 0x80) == 0
                || value.Length > 1025)
                throw new CryptographicException("The SSH private-key integer is invalid.");
            return new BigInteger(1, value);
        }
    }
}
