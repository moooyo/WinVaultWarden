using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Core.Passkeys;

public static class WebAuthnCrypto
{
    public static byte[] BuildAuthenticatorData(string rpId, uint signatureCounter, bool userPresent = true, bool userVerified = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rpId);

        var authenticatorData = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(rpId), authenticatorData.AsSpan(0, 32));
        authenticatorData[32] = BuildFlags(userPresent, userVerified);
        BinaryPrimitives.WriteUInt32BigEndian(authenticatorData.AsSpan(33, 4), signatureCounter);
        return authenticatorData;
    }

    public static byte[] SignAssertion(
        string pkcs8PrivateKeyBase64Url,
        ReadOnlySpan<byte> authenticatorData,
        ReadOnlySpan<byte> clientDataJson)
    {
        if (authenticatorData.Length == 0)
            throw new ArgumentException("Authenticator data is required.", nameof(authenticatorData));
        if (clientDataJson.Length == 0)
            throw new ArgumentException("Client data JSON is required.", nameof(clientDataJson));

        var privateKey = DecodeBase64Url(pkcs8PrivateKeyBase64Url);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(privateKey, out _);

        var clientDataHash = SHA256.HashData(clientDataJson);
        var signedData = new byte[authenticatorData.Length + clientDataHash.Length];
        authenticatorData.CopyTo(signedData);
        clientDataHash.CopyTo(signedData.AsSpan(authenticatorData.Length));

        return ecdsa.SignData(signedData, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    public static string EncodeBase64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static byte[] DecodeBase64Url(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Convert.FromBase64String(base64);
    }

    private static byte BuildFlags(bool userPresent, bool userVerified)
    {
        byte flags = 0;
        if (userPresent)
            flags |= 0x01;
        if (userVerified)
            flags |= 0x04;
        return flags;
    }
}
