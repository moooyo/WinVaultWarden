using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using Core.Services;

namespace Core.Passkeys;

public static class WebAuthnCrypto
{
    public static byte[] BuildAuthenticatorData(
        string rpId,
        uint signatureCounter,
        PasskeyApprovalEvidence evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rpId);

        var authenticatorData = new byte[37];
        SHA256.HashData(
            Encoding.UTF8.GetBytes(rpId),
            authenticatorData.AsSpan(0, 32));
        authenticatorData[32] = BuildFlags(evidence);
        BinaryPrimitives.WriteUInt32BigEndian(
            authenticatorData.AsSpan(33, 4),
            signatureCounter);
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

    private static byte BuildFlags(PasskeyApprovalEvidence evidence)
    {
        if (evidence.UserVerified && !evidence.UserPresent)
        {
            throw new ArgumentException(
                "UserVerified requires UserPresent.",
                nameof(evidence));
        }

        byte flags = 0;
        if (evidence.UserPresent)
            flags |= 0x01;
        if (evidence.UserVerified)
            flags |= 0x04;
        return flags;
    }

    // 生成 ES256(P-256) 密钥对:返回 (PKCS8 私钥 base64url, COSE_Key 字节, SPKI DER 字节)。
    public static (string Pkcs8PrivateKeyB64Url, byte[] CoseKey, byte[] SpkiDer) GenerateEs256KeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = ecdsa.ExportPkcs8PrivateKey();
        var spki = ecdsa.ExportSubjectPublicKeyInfo();
        var p = ecdsa.ExportParameters(false);
        var cose = EncodeCoseEc2(LeftPad32(p.Q.X!), LeftPad32(p.Q.Y!));
        return (EncodeBase64Url(pkcs8), cose, spki);
    }

    // authData(注册):rpIdHash(32)‖flags(1)‖counter(4)‖aaguid(16)‖credIdLen(2,BE)‖credId‖coseKey。
    public static byte[] BuildAttestedAuthenticatorData(
        string rpId,
        uint counter,
        PasskeyApprovalEvidence evidence,
        byte[] aaguid,
        byte[] credentialId,
        byte[] coseKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rpId);
        if (aaguid.Length != 16)
            throw new ArgumentException("aaguid must be 16 bytes.", nameof(aaguid));

        var head = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(rpId), head.AsSpan(0, 32));
        head[32] = (byte)(BuildFlags(evidence) | 0x40);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(33, 4), counter);

        var credLen = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(
            credLen,
            (ushort)credentialId.Length);

        var total = new byte[
            head.Length
            + aaguid.Length
            + credLen.Length
            + credentialId.Length
            + coseKey.Length];
        var offset = 0;
        foreach (var part in new[] { head, aaguid, credLen, credentialId, coseKey })
        {
            part.CopyTo(total.AsSpan(offset));
            offset += part.Length;
        }

        return total;
    }

    // attestationObject: CBOR {"fmt":"none","attStmt":{},"authData":<bstr>}。
    public static byte[] BuildNoneAttestationObject(byte[] authenticatorData)
    {
        var w = new CborWriter();
        w.WriteStartMap(3);
        w.WriteTextString("fmt"); w.WriteTextString("none");
        w.WriteTextString("attStmt"); w.WriteStartMap(0); w.WriteEndMap();
        w.WriteTextString("authData"); w.WriteByteString(authenticatorData);
        w.WriteEndMap();
        return w.Encode();
    }

    private static byte[] EncodeCoseEc2(byte[] x, byte[] y)
    {
        var w = new CborWriter();
        w.WriteStartMap(5);
        w.WriteInt32(1); w.WriteInt32(2);    // kty: EC2
        w.WriteInt32(3); w.WriteInt32(-7);   // alg: ES256
        w.WriteInt32(-1); w.WriteInt32(1);   // crv: P-256
        w.WriteInt32(-2); w.WriteByteString(x);
        w.WriteInt32(-3); w.WriteByteString(y);
        w.WriteEndMap();
        return w.Encode();
    }

    private static byte[] LeftPad32(byte[] value)
    {
        if (value.Length == 32) return value;
        if (value.Length > 32) throw new ArgumentException("EC coordinate longer than 32 bytes.");
        var padded = new byte[32];
        value.CopyTo(padded.AsSpan(32 - value.Length));
        return padded;
    }
}
