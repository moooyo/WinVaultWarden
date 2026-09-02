using System.Formats.Cbor;
using System.Security.Cryptography;
using Api.Dtos;
using Crypto.V2;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class V2AccountKeysValidatorTests
{
    [Fact]
    public void Validate_OfficialV2GraphAuthenticatesAllAccountKeys()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;
        using var fixture = CreateFixture();

        var result = new V2AccountKeysValidator().Validate(
            fixture.AccountKeys,
            fixture.UserKey);

        Assert.Equal(fixture.UserKey.KeyIdHex, result.UserKeyId);
        Assert.Equal(2, result.SecurityVersion);
        Assert.Equal(
            fixture.AccountKeys.PublicKeyEncryptionKeyPair!.WrappedPrivateKey,
            result.WrappedPrivateKey);
    }

    [Theory]
    [InlineData(nameof(PublicKeyEncryptionKeyPairResponse.WrappedPrivateKey))]
    [InlineData(nameof(SignatureKeyPairResponse.WrappedSigningKey))]
    [InlineData(nameof(AccountSecurityState.SecurityState))]
    [InlineData(nameof(AccountSecurityState.SecurityVersion))]
    public void Validate_MissingRequiredV2FieldFailsClosed(string field)
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;
        using var fixture = CreateFixture();
        var invalid = RemoveRequiredField(fixture.AccountKeys, field);

        Assert.Throws<InvalidDataException>(() =>
            new V2AccountKeysValidator().Validate(invalid, fixture.UserKey));
    }

    [Fact]
    public void Validate_TransitionalResponseWithoutOptionalPublicFieldsAuthenticatesCoreState()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;
        using var fixture = CreateFixture();
        var transitional = fixture.AccountKeys with
        {
            PublicKeyEncryptionKeyPair = fixture.AccountKeys.PublicKeyEncryptionKeyPair! with
            {
                PublicKey = null,
                SignedPublicKey = null,
            },
            SignatureKeyPair = fixture.AccountKeys.SignatureKeyPair! with
            {
                VerifyingKey = null,
            },
        };

        var result = new V2AccountKeysValidator().Validate(
            transitional,
            fixture.UserKey);

        Assert.Equal(fixture.UserKey.KeyIdHex, result.UserKeyId);
        Assert.Equal(2, result.SecurityVersion);
        Assert.Equal(
            fixture.AccountKeys.PublicKeyEncryptionKeyPair!.WrappedPrivateKey,
            result.WrappedPrivateKey);
    }

    [Theory]
    [InlineData(nameof(PublicKeyEncryptionKeyPairResponse.WrappedPrivateKey))]
    [InlineData(nameof(PublicKeyEncryptionKeyPairResponse.PublicKey))]
    [InlineData(nameof(PublicKeyEncryptionKeyPairResponse.SignedPublicKey))]
    [InlineData(nameof(SignatureKeyPairResponse.WrappedSigningKey))]
    [InlineData(nameof(SignatureKeyPairResponse.VerifyingKey))]
    [InlineData(nameof(AccountSecurityState.SecurityState))]
    public void Validate_NonCanonicalOfficialV2FieldFailsClosed(string field)
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;
        using var fixture = CreateFixture();
        var invalid = MakeFieldNonCanonical(fixture.AccountKeys, field);

        Assert.Throws<InvalidDataException>(() =>
            new V2AccountKeysValidator().Validate(invalid, fixture.UserKey));
    }

    [Fact]
    public void Validate_DerivedPublicKeyMismatchFailsClosed()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;
        using var fixture = CreateFixture();
        var differentPublicKey = Convert.FromBase64String(
            fixture.AccountKeys.PublicKeyEncryptionKeyPair!.PublicKey!);
        try
        {
            differentPublicKey[0] ^= 1;
            var invalid = fixture.AccountKeys with
            {
                PublicKeyEncryptionKeyPair = fixture.AccountKeys.PublicKeyEncryptionKeyPair! with
                {
                    PublicKey = Convert.ToBase64String(differentPublicKey),
                },
            };

            Assert.Throws<InvalidDataException>(() =>
                new V2AccountKeysValidator().Validate(invalid, fixture.UserKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(differentPublicKey);
        }
    }

    [Fact]
    public void Validate_DerivedVerifyingKeyMismatchFailsClosed()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;
        using var fixture = CreateFixture();
        using var otherSigningKey = AccountSigningKey.GenerateMldsa44();
        var invalid = fixture.AccountKeys with
        {
            SignatureKeyPair = fixture.AccountKeys.SignatureKeyPair! with
            {
                VerifyingKey = otherSigningKey.ExportVerifyingKeyCoseBase64(),
            },
        };

        Assert.Throws<InvalidDataException>(() =>
            new V2AccountKeysValidator().Validate(invalid, fixture.UserKey));
    }

    [Fact]
    public void Validate_SignedPublicKeyPayloadMismatchFailsClosed()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;
        using var fixture = CreateFixture();
        using var signingKey = ImportSigningKey(fixture);
        var wrongPublicKey = RandomNumberGenerator.GetBytes(294);
        var payload = EncodeSignedPublicKeyPayload(wrongPublicKey);
        try
        {
            var invalidSignedPublicKey = CoseSign1Codec.SignToBase64(
                payload,
                signingKey,
                BitwardenSigningNamespace.SignedPublicKey);
            var invalid = fixture.AccountKeys with
            {
                PublicKeyEncryptionKeyPair = fixture.AccountKeys.PublicKeyEncryptionKeyPair! with
                {
                    SignedPublicKey = invalidSignedPublicKey,
                },
            };

            Assert.Throws<InvalidDataException>(() =>
                new V2AccountKeysValidator().Validate(invalid, fixture.UserKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrongPublicKey);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    [Theory]
    [InlineData(nameof(PublicKeyEncryptionKeyPairResponse.SignedPublicKey))]
    [InlineData(nameof(AccountSecurityState.SecurityState))]
    public void Validate_TamperedPresentSignatureFailsClosed(string field)
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;
        using var fixture = CreateFixture();
        var invalid = CorruptSignature(fixture.AccountKeys, field);

        Assert.Throws<InvalidDataException>(() =>
            new V2AccountKeysValidator().Validate(invalid, fixture.UserKey));
    }

    [Fact]
    public void Validate_SignedSecurityVersionMismatchFailsClosed()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;
        using var fixture = CreateFixture();
        using var signingKey = ImportSigningKey(fixture);
        var payload = EncodeSecurityStatePayload(1);
        try
        {
            var invalid = fixture.AccountKeys with
            {
                SecurityState = fixture.AccountKeys.SecurityState! with
                {
                    SecurityState = CoseSign1Codec.SignToBase64(
                        payload,
                        signingKey,
                        BitwardenSigningNamespace.SecurityState),
                },
            };

            Assert.Throws<InvalidDataException>(() =>
                new V2AccountKeysValidator().Validate(invalid, fixture.UserKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Validate_UnsignedSecurityVersionMismatchFailsClosed(int version)
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;
        using var fixture = CreateFixture();
        var invalid = fixture.AccountKeys with
        {
            SecurityState = fixture.AccountKeys.SecurityState! with
            {
                SecurityVersion = version,
            },
        };

        Assert.Throws<InvalidDataException>(() =>
            new V2AccountKeysValidator().Validate(invalid, fixture.UserKey));
    }

    [Fact]
    public void Validate_SigningKeyDecryptFailureClearsAlreadyDecryptedPrivateKey()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;
        using var fixture = CreateFixture();
        var privateKey = fixture.UserKey.Decrypt(
            fixture.AccountKeys.PublicKeyEncryptionKeyPair!.WrappedPrivateKey!,
            CoseContentFormat.Pkcs8PrivateKey);
        Assert.Contains(privateKey, value => value != 0);
        var validator = new V2AccountKeysValidator((_, _, contentFormat) =>
            contentFormat == CoseContentFormat.Pkcs8PrivateKey
                ? privateKey
                : throw new CryptographicException("simulated signing-key failure"));

        Assert.Throws<InvalidDataException>(() =>
            validator.Validate(fixture.AccountKeys, fixture.UserKey));

        Assert.All(privateKey, value => Assert.Equal(0, value));
    }

    private static Fixture CreateFixture()
    {
        var userKey = V2UserKey.Generate();
        using var rsa = RSA.Create(2048);
        using var signingKey = AccountSigningKey.GenerateMldsa44();
        var privateKey = rsa.ExportPkcs8PrivateKey();
        var publicKey = rsa.ExportSubjectPublicKeyInfo();
        var signingKeyCose = signingKey.ExportSigningKeyCose();
        var signedPublicKeyPayload = EncodeSignedPublicKeyPayload(publicKey);
        var securityStatePayload = EncodeSecurityStatePayload();
        try
        {
            var wrappedPrivateKey = userKey.Encrypt(
                privateKey,
                CoseContentFormat.Pkcs8PrivateKey);
            var wrappedSigningKey = userKey.Encrypt(
                signingKeyCose,
                CoseContentFormat.CoseKey);
            var signedPublicKey = CoseSign1Codec.SignToBase64(
                signedPublicKeyPayload,
                signingKey,
                BitwardenSigningNamespace.SignedPublicKey);
            var signedSecurityState = CoseSign1Codec.SignToBase64(
                securityStatePayload,
                signingKey,
                BitwardenSigningNamespace.SecurityState);
            return new Fixture(
                userKey,
                new AccountKeysResponse(
                    new PublicKeyEncryptionKeyPairResponse(
                        wrappedPrivateKey,
                        Convert.ToBase64String(publicKey),
                        signedPublicKey),
                    new SignatureKeyPairResponse(
                        wrappedSigningKey,
                        signingKey.ExportVerifyingKeyCoseBase64()),
                    new AccountSecurityState(signedSecurityState, 2)));
        }
        catch
        {
            userKey.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(publicKey);
            CryptographicOperations.ZeroMemory(signingKeyCose);
            CryptographicOperations.ZeroMemory(signedPublicKeyPayload);
            CryptographicOperations.ZeroMemory(securityStatePayload);
        }
    }

    private static AccountSigningKey ImportSigningKey(Fixture fixture)
    {
        var wrapped = fixture.AccountKeys.SignatureKeyPair!.WrappedSigningKey!;
        var signingKey = fixture.UserKey.Decrypt(wrapped, CoseContentFormat.CoseKey);
        try
        {
            return AccountSigningKey.ImportSigningKeyCose(signingKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingKey);
        }
    }

    private static AccountKeysResponse RemoveRequiredField(
        AccountKeysResponse accountKeys,
        string field) => field switch
    {
        nameof(PublicKeyEncryptionKeyPairResponse.WrappedPrivateKey) => accountKeys with
        {
            PublicKeyEncryptionKeyPair = accountKeys.PublicKeyEncryptionKeyPair! with
            {
                WrappedPrivateKey = null,
            },
        },
        nameof(PublicKeyEncryptionKeyPairResponse.PublicKey) => accountKeys with
        {
            PublicKeyEncryptionKeyPair = accountKeys.PublicKeyEncryptionKeyPair! with
            {
                PublicKey = null,
            },
        },
        nameof(PublicKeyEncryptionKeyPairResponse.SignedPublicKey) => accountKeys with
        {
            PublicKeyEncryptionKeyPair = accountKeys.PublicKeyEncryptionKeyPair! with
            {
                SignedPublicKey = null,
            },
        },
        nameof(SignatureKeyPairResponse.WrappedSigningKey) => accountKeys with
        {
            SignatureKeyPair = accountKeys.SignatureKeyPair! with
            {
                WrappedSigningKey = null,
            },
        },
        nameof(SignatureKeyPairResponse.VerifyingKey) => accountKeys with
        {
            SignatureKeyPair = accountKeys.SignatureKeyPair! with
            {
                VerifyingKey = null,
            },
        },
        nameof(AccountSecurityState.SecurityState) => accountKeys with
        {
            SecurityState = accountKeys.SecurityState! with
            {
                SecurityState = null,
            },
        },
        nameof(AccountSecurityState.SecurityVersion) => accountKeys with
        {
            SecurityState = accountKeys.SecurityState! with
            {
                SecurityVersion = 0,
            },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static AccountKeysResponse MakeFieldNonCanonical(
        AccountKeysResponse accountKeys,
        string field) => field switch
    {
        nameof(PublicKeyEncryptionKeyPairResponse.WrappedPrivateKey) => accountKeys with
        {
            PublicKeyEncryptionKeyPair = accountKeys.PublicKeyEncryptionKeyPair! with
            {
                WrappedPrivateKey =
                    accountKeys.PublicKeyEncryptionKeyPair!.WrappedPrivateKey! + "\n",
            },
        },
        nameof(PublicKeyEncryptionKeyPairResponse.PublicKey) => accountKeys with
        {
            PublicKeyEncryptionKeyPair = accountKeys.PublicKeyEncryptionKeyPair! with
            {
                PublicKey = accountKeys.PublicKeyEncryptionKeyPair!.PublicKey! + "\n",
            },
        },
        nameof(PublicKeyEncryptionKeyPairResponse.SignedPublicKey) => accountKeys with
        {
            PublicKeyEncryptionKeyPair = accountKeys.PublicKeyEncryptionKeyPair! with
            {
                SignedPublicKey =
                    accountKeys.PublicKeyEncryptionKeyPair!.SignedPublicKey! + "\n",
            },
        },
        nameof(SignatureKeyPairResponse.WrappedSigningKey) => accountKeys with
        {
            SignatureKeyPair = accountKeys.SignatureKeyPair! with
            {
                WrappedSigningKey = accountKeys.SignatureKeyPair!.WrappedSigningKey! + "\n",
            },
        },
        nameof(SignatureKeyPairResponse.VerifyingKey) => accountKeys with
        {
            SignatureKeyPair = accountKeys.SignatureKeyPair! with
            {
                VerifyingKey = accountKeys.SignatureKeyPair!.VerifyingKey! + "\n",
            },
        },
        nameof(AccountSecurityState.SecurityState) => accountKeys with
        {
            SecurityState = accountKeys.SecurityState! with
            {
                SecurityState = accountKeys.SecurityState!.SecurityState! + "\n",
            },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static AccountKeysResponse CorruptSignature(
        AccountKeysResponse accountKeys,
        string field)
    {
        var original = field switch
        {
            nameof(PublicKeyEncryptionKeyPairResponse.SignedPublicKey) =>
                accountKeys.PublicKeyEncryptionKeyPair!.SignedPublicKey!,
            nameof(AccountSecurityState.SecurityState) =>
                accountKeys.SecurityState!.SecurityState!,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var encoded = Convert.FromBase64String(original);
        try
        {
            encoded[^1] ^= 1;
            var corrupted = Convert.ToBase64String(encoded);
            return field switch
            {
                nameof(PublicKeyEncryptionKeyPairResponse.SignedPublicKey) => accountKeys with
                {
                    PublicKeyEncryptionKeyPair = accountKeys.PublicKeyEncryptionKeyPair! with
                    {
                        SignedPublicKey = corrupted,
                    },
                },
                nameof(AccountSecurityState.SecurityState) => accountKeys with
                {
                    SecurityState = accountKeys.SecurityState! with
                    {
                        SecurityState = corrupted,
                    },
                },
                _ => throw new ArgumentOutOfRangeException(nameof(field)),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static byte[] EncodeSignedPublicKeyPayload(ReadOnlySpan<byte> publicKey)
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(3);
        writer.WriteTextString("algorithm");
        writer.WriteInt32(0);
        writer.WriteTextString("contentFormat");
        writer.WriteInt32(0);
        writer.WriteTextString("publicKey");
        writer.WriteByteString(publicKey);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private static byte[] EncodeSecurityStatePayload(int version = 2)
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(1);
        writer.WriteTextString("version");
        writer.WriteInt32(version);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private sealed record Fixture(
        V2UserKey UserKey,
        AccountKeysResponse AccountKeys) : IDisposable
    {
        public void Dispose() => UserKey.Dispose();
    }
}
