using System.Security.Cryptography;
using System.Text;
using System.Numerics;

namespace Core.SshAgent;

public sealed record SshAgentPublicKey(string Algorithm, byte[] Blob)
{
    public string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Blob)).TrimEnd('=');

    public static SshAgentPublicKey Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > 24_000)
            throw new InvalidDataException("An SSH public key is too large.");
        var fields = text.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
            throw new InvalidDataException("An OpenSSH public key is required.");
        var blob = Convert.FromBase64String(fields[1]);
        if (blob.Length > SshAgentProtocol.MaximumPublicKeyLength)
            throw new InvalidDataException("An SSH public key is too large.");
        var reader = new SshAgentReader(blob);
        var algorithm = Encoding.ASCII.GetString(reader.ReadString(64));
        if (algorithm != fields[0])
            throw new InvalidDataException("SSH public-key algorithm names do not match.");
        switch (algorithm)
        {
            case "ssh-ed25519":
                if (reader.ReadString(32).Length != 32)
                    throw new InvalidDataException("Invalid Ed25519 public key.");
                break;
            case "ssh-rsa":
                var exponent = reader.ReadString(8);
                var modulus = reader.ReadString(1_025);
                if (!IsCanonicalPositive(exponent) || !IsCanonicalPositive(modulus))
                    throw new InvalidDataException("Invalid or weak RSA public key.");
                var e = new BigInteger(exponent, isUnsigned: false, isBigEndian: true);
                var n = new BigInteger(modulus, isUnsigned: false, isBigEndian: true);
                if (e < 3 || e.IsEven || n.GetBitLength() is < 2_048 or > 8_192 || n.IsEven)
                    throw new InvalidDataException("Invalid or weak RSA public key.");
                break;
            case "ecdsa-sha2-nistp256":
                ReadEc(ref reader, "nistp256", 65);
                break;
            case "ecdsa-sha2-nistp384":
                ReadEc(ref reader, "nistp384", 97);
                break;
            case "ecdsa-sha2-nistp521":
                ReadEc(ref reader, "nistp521", 133);
                break;
            default:
                throw new NotSupportedException("Unsupported SSH key algorithm.");
        }
        reader.RequireEnd();
        return new SshAgentPublicKey(algorithm, blob);
    }

    private static void ReadEc(ref SshAgentReader reader, string expectedCurve, int pointLength)
    {
        if (Encoding.ASCII.GetString(reader.ReadString(16)) != expectedCurve)
            throw new InvalidDataException("Invalid SSH elliptic-curve name.");
        var point = reader.ReadString(pointLength);
        if (point.Length != pointLength || point[0] != 4)
            throw new InvalidDataException("Invalid SSH public point.");
    }

    private static bool IsCanonicalPositive(ReadOnlySpan<byte> value) =>
        value.Length != 0 && (value[0] & 0x80) == 0
        && (value[0] != 0 || value.Length > 1 && (value[1] & 0x80) != 0);
}
