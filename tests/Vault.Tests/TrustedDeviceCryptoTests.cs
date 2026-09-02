using System.Security.Cryptography;
using Core.Enums;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class TrustedDeviceCryptoTests
{
    private const string SafeFailureMessage = "Trusted device cryptographic material is invalid.";

    private readonly CryptoService _crypto = new();

    [Fact]
    public void CreateTrustMaterial_ProducesOfficialTrustedDeviceKeyGraph()
    {
        using var userKey = CreateUserKey();
        var sut = new TrustedDeviceCrypto(_crypto);
        using var material = sut.CreateTrustMaterial(userKey);

        Assert.Equal(TrustedDeviceCrypto.DeviceKeyLength, material.DeviceKey.FullKey.Length);
        Assert.Equal(EncryptionType.Rsa2048_OaepSha1_B64, material.EncryptedUserKey.Type);
        Assert.Equal(EncryptionType.AesCbc256_HmacSha256_B64, material.EncryptedPublicKey.Type);
        Assert.Equal(EncryptionType.AesCbc256_HmacSha256_B64, material.EncryptedPrivateKey.Type);

        byte[]? publicKey = null;
        byte[]? privateKey = null;
        byte[]? publicKeyFromPrivate = null;
        byte[]? decryptedUserKey = null;

        try
        {
            publicKey = _crypto.Decrypt(material.EncryptedPublicKey, userKey);
            privateKey = _crypto.Decrypt(material.EncryptedPrivateKey, material.DeviceKey);

            using var publicRsa = RSA.Create();
            publicRsa.ImportSubjectPublicKeyInfo(publicKey, out var publicBytesRead);
            using var privateRsa = RSA.Create();
            privateRsa.ImportPkcs8PrivateKey(privateKey, out var privateBytesRead);

            Assert.Equal(publicKey.Length, publicBytesRead);
            Assert.Equal(privateKey.Length, privateBytesRead);
            Assert.Equal(TrustedDeviceCrypto.RsaKeySizeBits, publicRsa.KeySize);
            Assert.Equal(TrustedDeviceCrypto.RsaKeySizeBits, privateRsa.KeySize);

            publicKeyFromPrivate = privateRsa.ExportSubjectPublicKeyInfo();
            Assert.Equal(publicKey, publicKeyFromPrivate);

            decryptedUserKey = _crypto.DecryptRsa(material.EncryptedUserKey, privateKey);
            Assert.Equal(userKey.FullKey, decryptedUserKey);
        }
        finally
        {
            Zero(publicKey);
            Zero(privateKey);
            Zero(publicKeyFromPrivate);
            Zero(decryptedUserKey);
        }
    }

    [Fact]
    public void DecryptUserKey_RoundTripsGeneratedMaterial()
    {
        using var userKey = CreateUserKey();
        var sut = new TrustedDeviceCrypto(_crypto);
        using var material = sut.CreateTrustMaterial(userKey);

        using var recovered = sut.DecryptUserKey(
            material.DeviceKey,
            material.EncryptedPrivateKey,
            material.EncryptedUserKey);

        Assert.Equal(userKey.FullKey, recovered.FullKey);
    }

    [Theory]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64)]
    public void DecryptUserKey_OfficialBitwardenSdkCiphertextSupportsSha1EnvelopeVariants(
        EncryptionType envelopeType)
    {
        var deviceKeyBytes = Convert.FromHexString(
            "72EB3C73AC9CCB91C382D7FA5892D7E60C6DF5DE36D9FFD3DD69E6EC4134D185" +
            "4CD071FEC2D89C13E63E205D5707909C758EFA20B676BB08F707CBC94193CEF7");
        var expectedUserKey = Convert.FromHexString(
            "6D80AC93CE7B865F10249B71C912BAE6D8D4ADBC4A0B868389F269B2697E348B" +
            "F85BD715805BE2DEA543FB2253514D93E14C0D29662DB7DA6A59FED0FB65820A");
        const string encryptedUserKey =
            "4.f+VbbacRhO2q4MOUSdt1AIjQ2FuLAvg4aDxJMXAh3VxvbmUADj8Ct/R7XEpPUqApmbRS566jS0eRVy8Sk08ogoCdj1IFN9VsIk" +
            "y2i2X1WHK1fUnr3UBmXE3tl2NPBbx56U+h73S2jNTSyet2W18Jg2q7/w8KIhR3J41QrG9aGoOTN93to3hb5W4z6rdrSI0e7Gkizb" +
            "wcIA0NH7Z1JyAhrjPm9+tjRjg060YbEbGaWTAOkZWfgbLjr8bY455DteO2xxG139cOx7EBo66N+YhjsLi0ozkeUyPQkoWBdKMcQl" +
            "lS7jCfB4fDyJA05ALTbk74syKkvqFxqwmQbg+aVn+dcw==";
        const string encryptedPrivateKey =
            "2.GyQfUYWW6Byy4UV5icFLxg==|EMiU7OTF79N6tfv3+YUs5zJhBAgqv6sa5YCoPl6yAETh7Tfk+JmbeizxXFPj5Q1X/tcVpDZl/" +
            "3fGcxtnIxg1YtvDFn7j8uPnoApOWhCKmwcvJSIkt+qvX3lELNBwZXozSiy7PbQ0JbCMe2d4MkimR5k8+lE9FB3208yYK7nOJhlrs" +
            "UCnOekCYEU9/4NCMA8tz8SpITx/MN4JJ1TQ/KjPJYLt+3JNUxK47QlgREWQvyVzCRt7ZGtcgIJ/U1qycAWMpEg9NkuV8j5QRA1S7" +
            "VBsA6qliJwys5+dmTuIOmOMwdKFZDc4ZvWoRkPp2TSJBu7L8sSAgU6mmDWac8iQ+9Ka/drdfwYLrH8GAZvURk79tSpRrT7+PAFe2" +
            "QdUtliUIyiqkh8iJVjZube4hRnEsRuX9V9b+UdtAr6zAj7mugO/VAu5T9J38V79V2ohG3NtXysDeKLXpAlkhjllWXeq/wret2fD4" +
            "WiwqEDj0G2A/PY3F3OziIgp0UKc00AfqrPq8OVK3A+aowwVqdYadgxyoVCKWJ8unJeAXG7MrMQ9tHpzF6COoaEy7Wwoc17qko33z" +
            "azwLZbfAjB4oc8Ea26jRKnJZP56sVZAjOSQQMziAsA08MRaa/DQhgRea1+Ygba0gMft8Dww8anN2gQBveTZRBWyqXYgN3U0Ity5g" +
            "NauT8RnFk9faqVFt2Qxnp0JgJ+PsqEt5Hn4avBRZQQ7o8VvPnxYLDKFe3I2m6HFYFWRhOGeDYxexIuaiF2iIAYFVUmnDuWpgnUiL" +
            "4XJ3KHDsjkPzcV3z4D2Knr/El2VVXve8jhDjETfovmmN28+i2e29PXvKIymTskMFpFCQPc7wBY/Id7pmgb3SujKYNpkAS2sByDoR" +
            "ir0my49DDGfta0dENssJhFd3x+87fZbEj3cMiikg2pBwpTLgmfIUa5cVZU2s8JZ9wu7gaioYzvX+elHa3EHLcnEUoJTtSf9kjb+N" +
            "bq4ktMgYAO2wIC96t1LvmqK4Qn2cOdw5QNlRqALhqe5V31kyIcwRMK0AyIoOPhnSqtpYdFiR3LDTvZA8dU0vSsuchCwHNMeRUtKv" +
            "dzN/tk+oeznyY/mpakUESN501lEKd/QFLtJZsDZTtNlcA8fU3kDtws4ZIMR0O5+PFmgQFSU8OMobf9ClUzy/wHTvYGyDuSwbOoPe" +
            "S955QKkUKXCNMj33yrPr+ioHQ1BNwLX3VmMF4bNRBY/vr+CG0/EZi0Gwl0kyHGl0yWEtpQuu+/PaROJeOraWy5D1UoZZhY4n0zJZ" +
            "Bt1eg3FZ2rhKv4gdUc50nZpeNWE8pIqZ6RQ7qPJuqfF1Z+G73iOSnLYCHDiiFmhD5ivf9IGkTAcWcBsQ/2wcSj9bFJr4DrKfsbQ4" +
            "CkSWICWVn/W+InKkO6BTsBbYmvte5SvbaN+UOtiUSkHLBCCr8273VNgcB/hgtbUires3noxYZJxoczr+i7vdlEgQnWEKrpo0Cifs" +
            "FxGwYS3Yy2K79iwvDMaLPDf73zLSbuoUl6602F2Mzcjnals67f+gSpaDvWt7Kg9c/ZfGjq8oNxVaXJnX3gSDsO+fhwVAtnDApL+t" +
            "L8cFfxGerW4KGi9/74woH+C3MMIViBtNnrpEuvxUW97Dg5nd40oGDeyi/q+8HdcxkneyFY=|JYdol19Yi+n1r7M+06EwK5JCi2s/" +
            "CWqKui2Cy6hEb3k=";

        try
        {
            using var deviceKey = new SymmetricCryptoKey(deviceKeyBytes);
            deviceKeyBytes = Array.Empty<byte>();
            var selectedEncryptedUserKey = envelopeType.HasLegacyRsaMac()
                ? $"{(int)envelopeType}.{encryptedUserKey[2..]}|{Convert.ToBase64String(new byte[EncString.LegacyRsaMacLength])}"
                : encryptedUserKey;
            using var recovered = new TrustedDeviceCrypto(_crypto).DecryptUserKey(
                deviceKey,
                EncString.Parse(encryptedPrivateKey),
                EncString.Parse(selectedEncryptedUserKey));

            Assert.Equal(expectedUserKey, recovered.FullKey);
        }
        finally
        {
            Zero(deviceKeyBytes);
            Zero(expectedUserKey);
        }
    }

    [Fact]
    public void CreateTrustMaterial_RejectsUnauthenticatedUserKey()
    {
        using var shortKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(32));
        var sut = new TrustedDeviceCrypto(_crypto);

        AssertSafeFailure(() => sut.CreateTrustMaterial(shortKey));
    }

    [Fact]
    public void CreateTrustMaterial_RejectsInconsistentUserKeyViews()
    {
        using var userKey = CreateUserKey();
        userKey.EncKey[0] ^= 0x80;
        var sut = new TrustedDeviceCrypto(_crypto);

        AssertSafeFailure(() => sut.CreateTrustMaterial(userKey));
    }

    [Fact]
    public void DecryptUserKey_RejectsUnauthenticatedDeviceKey()
    {
        using var userKey = CreateUserKey();
        using var shortDeviceKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(32));
        var sut = new TrustedDeviceCrypto(_crypto);
        using var material = sut.CreateTrustMaterial(userKey);

        AssertSafeFailure(() => sut.DecryptUserKey(
            shortDeviceKey,
            material.EncryptedPrivateKey,
            material.EncryptedUserKey));
    }

    [Fact]
    public void DecryptUserKey_RejectsUnexpectedEncStringTypes()
    {
        using var userKey = CreateUserKey();
        var sut = new TrustedDeviceCrypto(_crypto);
        using var material = sut.CreateTrustMaterial(userKey);

        AssertSafeFailure(() => sut.DecryptUserKey(
            material.DeviceKey,
            material.EncryptedUserKey,
            material.EncryptedUserKey));
        AssertSafeFailure(() => sut.DecryptUserKey(
            material.DeviceKey,
            material.EncryptedPrivateKey,
            material.EncryptedPrivateKey));
    }

    [Fact]
    public void DecryptUserKey_RejectsMalformedLegacyRsaMac()
    {
        using var userKey = CreateUserKey();
        var sut = new TrustedDeviceCrypto(_crypto);
        using var material = sut.CreateTrustMaterial(userKey);
        var malformed = new EncString(
            EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64,
            [],
            material.EncryptedUserKey.Ct.ToArray(),
            new byte[EncString.LegacyRsaMacLength - 1]);

        AssertSafeFailure(() => sut.DecryptUserKey(
            material.DeviceKey,
            material.EncryptedPrivateKey,
            malformed));
    }

    [Theory]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64)]
    public void DecryptUserKey_AcceptsHistoricalOaepSha256Envelope(
        EncryptionType envelopeType)
    {
        using var userKey = CreateUserKey();
        using var deviceKey = new SymmetricCryptoKey(
            RandomNumberGenerator.GetBytes(64));
        using var rsa = RSA.Create(2048);
        var privateKey = rsa.ExportPkcs8PrivateKey();
        try
        {
            var encryptedPrivateKey = _crypto.Encrypt(privateKey, deviceKey);
            var encryptedUserKey = new EncString(
                envelopeType,
                [],
                rsa.Encrypt(userKey.FullKey, RSAEncryptionPadding.OaepSHA256),
                envelopeType.HasLegacyRsaMac()
                    ? new byte[EncString.LegacyRsaMacLength]
                    : null);
            var sut = new TrustedDeviceCrypto(_crypto);

            using var decrypted = sut.DecryptUserKey(
                deviceKey,
                encryptedPrivateKey,
                encryptedUserKey);

            Assert.Equal(userKey.FullKey, decrypted.FullKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    [Fact]
    public void TrustedDeviceOperations_RejectDisposedKeys()
    {
        var disposedUserKey = CreateUserKey();
        disposedUserKey.Dispose();
        var sut = new TrustedDeviceCrypto(_crypto);

        AssertSafeFailure(() => sut.CreateTrustMaterial(disposedUserKey));

        using var userKey = CreateUserKey();
        using var material = sut.CreateTrustMaterial(userKey);
        material.DeviceKey.Dispose();
        AssertSafeFailure(() => sut.DecryptUserKey(
            material.DeviceKey,
            material.EncryptedPrivateKey,
            material.EncryptedUserKey));
    }

    [Fact]
    public void DecryptUserKey_RejectsTamperedPrivateKeyEnvelope()
    {
        using var userKey = CreateUserKey();
        var sut = new TrustedDeviceCrypto(_crypto);
        using var material = sut.CreateTrustMaterial(userKey);
        var tamperedMac = material.EncryptedPrivateKey.Mac!.ToArray();
        tamperedMac[0] ^= 0x80;
        var tampered = new EncString(
            material.EncryptedPrivateKey.Type,
            material.EncryptedPrivateKey.Iv.ToArray(),
            material.EncryptedPrivateKey.Ct.ToArray(),
            tamperedMac);

        AssertSafeFailure(() => sut.DecryptUserKey(
            material.DeviceKey,
            tampered,
            material.EncryptedUserKey));
    }

    [Fact]
    public void DecryptUserKey_RejectsUserKeyWithWrongLength()
    {
        using var userKey = CreateUserKey();
        var sut = new TrustedDeviceCrypto(_crypto);
        using var material = sut.CreateTrustMaterial(userKey);
        var wrongLengthUserKey = RandomNumberGenerator.GetBytes(32);
        byte[]? devicePublicKey = null;

        try
        {
            devicePublicKey = _crypto.Decrypt(material.EncryptedPublicKey, userKey);
            var encryptedWrongLengthKey = _crypto.EncryptRsa(wrongLengthUserKey, devicePublicKey);

            AssertSafeFailure(() => sut.DecryptUserKey(
                material.DeviceKey,
                material.EncryptedPrivateKey,
                encryptedWrongLengthKey));
        }
        finally
        {
            Zero(wrongLengthUserKey);
            Zero(devicePublicKey);
        }
    }

    [Fact]
    public void DecryptUserKey_RejectsNon2048DevicePrivateKey()
    {
        using var userKey = CreateUserKey();
        var sut = new TrustedDeviceCrypto(_crypto);
        using var material = sut.CreateTrustMaterial(userKey);
        using var rsa = RSA.Create(3072);
        var privateKey = rsa.ExportPkcs8PrivateKey();

        try
        {
            var encryptedPrivateKey = _crypto.Encrypt(privateKey, material.DeviceKey);

            AssertSafeFailure(() => sut.DecryptUserKey(
                material.DeviceKey,
                encryptedPrivateKey,
                material.EncryptedUserKey));
        }
        finally
        {
            Zero(privateKey);
        }
    }

    [Fact]
    public void TrustMaterial_DisposeZerosOwnedDeviceKey()
    {
        using var userKey = CreateUserKey();
        var material = new TrustedDeviceCrypto(_crypto).CreateTrustMaterial(userKey);
        var ownedDeviceKey = material.DeviceKey;

        material.Dispose();

        Assert.All(ownedDeviceKey.FullKey, value => Assert.Equal(0, value));
        Assert.All(ownedDeviceKey.EncKey, value => Assert.Equal(0, value));
        Assert.NotNull(ownedDeviceKey.MacKey);
        Assert.All(ownedDeviceKey.MacKey!, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => _ = material.DeviceKey);
    }

    private static SymmetricCryptoKey CreateUserKey() =>
        new(Enumerable.Range(0, TrustedDeviceCrypto.UserKeyLength).Select(index => (byte)index).ToArray());

    private static void AssertSafeFailure(Action action)
    {
        var exception = Assert.Throws<CryptographicException>(action);
        Assert.Equal(SafeFailureMessage, exception.Message);
        Assert.Null(exception.InnerException);
    }

    private static void Zero(byte[]? value)
    {
        if (value is not null)
            CryptographicOperations.ZeroMemory(value);
    }
}
