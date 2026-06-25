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
}
