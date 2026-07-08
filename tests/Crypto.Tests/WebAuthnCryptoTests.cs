using System.Security.Cryptography;
using System.Text;
using Core.Passkeys;
using Xunit;

namespace Crypto.Tests;

public class WebAuthnCryptoTests
{
    [Fact]
    public void BuildAuthenticatorData_HashesRpIdAndWritesFlagsAndCounter()
    {
        var authData = WebAuthnCrypto.BuildAuthenticatorData("github.com", 42, userPresent: true, userVerified: true);

        Assert.Equal(37, authData.Length);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes("github.com")), authData[..32]);
        Assert.Equal(0x05, authData[32]);
        Assert.Equal(new byte[] { 0, 0, 0, 42 }, authData[33..37]);
    }

    [Fact]
    public void SignAssertion_ImportsBitwardenPkcs8KeyValueAndProducesValidDerSignature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyValue = WebAuthnCrypto.EncodeBase64Url(key.ExportPkcs8PrivateKey());
        var authData = WebAuthnCrypto.BuildAuthenticatorData("github.com", 1);
        var clientDataJson = Encoding.UTF8.GetBytes("""{"type":"webauthn.get","challenge":"AQID","origin":"https://github.com"}""");

        var signature = WebAuthnCrypto.SignAssertion(keyValue, authData, clientDataJson);

        var clientDataHash = SHA256.HashData(clientDataJson);
        var signedData = authData.Concat(clientDataHash).ToArray();
        Assert.True(key.VerifyData(signedData, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public void Base64Url_RoundTripsWithoutPadding()
    {
        var encoded = WebAuthnCrypto.EncodeBase64Url([1, 2, 3, 4, 5]);

        Assert.DoesNotContain("=", encoded);
        Assert.DoesNotContain("+", encoded);
        Assert.DoesNotContain("/", encoded);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, WebAuthnCrypto.DecodeBase64Url(encoded));
    }

    [Fact]
    public void GenerateEs256KeyPair_ProducesValidCoseKey_And_ImportableSpki()
    {
        var (pkcs8, cose, spki) = WebAuthnCrypto.GenerateEs256KeyPair();
        Assert.False(string.IsNullOrWhiteSpace(pkcs8));

        // COSE_Key: {1:2, 3:-7, -1:1, -2:x(32), -3:y(32)}
        var r = new System.Formats.Cbor.CborReader(cose);
        Assert.Equal(5, r.ReadStartMap());
        var map = new Dictionary<int, object>();
        for (int i = 0; i < 5; i++)
        {
            var k = r.ReadInt32();
            if (k is 1 or 3 or -1) map[k] = r.ReadInt32();
            else map[k] = r.ReadByteString();
        }
        r.ReadEndMap();
        Assert.Equal(2, map[1]);    // kty EC2
        Assert.Equal(-7, map[3]);   // alg ES256
        Assert.Equal(1, map[-1]);   // crv P-256
        Assert.Equal(32, ((byte[])map[-2]).Length);
        Assert.Equal(32, ((byte[])map[-3]).Length);

        // SPKI imports as P-256, and pairs with the PKCS8 private key (sign→verify round-trip).
        using var pub = System.Security.Cryptography.ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(spki, out _);
        Assert.Equal("nistP256", pub.ExportParameters(false).Curve.Oid.FriendlyName);
        var sig = WebAuthnCrypto.SignAssertion(pkcs8, new byte[37], new byte[] { 1, 2, 3 });
        var signed = new byte[37 + 32];
        System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 }).CopyTo(signed.AsSpan(37));
        Assert.True(pub.VerifyData(signed, sig, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public void BuildAttestedAuthenticatorData_HasRpHash_ATFlag_ZeroCounter_AndAttestedData()
    {
        var (_, cose, _) = WebAuthnCrypto.GenerateEs256KeyPair();
        var credId = new byte[] { 9, 8, 7, 6, 5 };
        var aaguid = new byte[16];
        var authData = WebAuthnCrypto.BuildAttestedAuthenticatorData("github.com", 0, true, true, aaguid, credId, cose);

        var expectedHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("github.com"));
        Assert.Equal(expectedHash, authData[..32]);
        Assert.Equal(0x01 | 0x04 | 0x40, authData[32]);       // UP|UV|AT
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, authData[33..37]);
        Assert.Equal(aaguid, authData[37..53]);
        Assert.Equal(credId.Length, (authData[53] << 8) | authData[54]); // credIdLen big-endian
        Assert.Equal(credId, authData[55..(55 + credId.Length)]);
        Assert.Equal(cose, authData[(55 + credId.Length)..]);
    }

    [Fact]
    public void BuildNoneAttestationObject_IsCborMap_None_EmptyAttStmt_AuthData()
    {
        var authData = new byte[] { 1, 2, 3, 4 };
        var obj = WebAuthnCrypto.BuildNoneAttestationObject(authData);
        var r = new System.Formats.Cbor.CborReader(obj);
        Assert.Equal(3, r.ReadStartMap());
        Assert.Equal("fmt", r.ReadTextString()); Assert.Equal("none", r.ReadTextString());
        Assert.Equal("attStmt", r.ReadTextString()); Assert.Equal(0, r.ReadStartMap()); r.ReadEndMap();
        Assert.Equal("authData", r.ReadTextString()); Assert.Equal(authData, r.ReadByteString());
        r.ReadEndMap();
    }
}
