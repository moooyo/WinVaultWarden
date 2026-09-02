using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Dtos;
using Core.Enums;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class ProviderOrganizationDecryptorTests
{
    [Theory]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64)]
    public void Decrypt_HistoricalProviderKeyTypes_DecryptProviderOrganization(
        EncryptionType providerKeyType)
    {
        using var graph = new ProviderGraph();
        var encryptedProviderKey = graph.WrapProviderKey(graph.ProviderKey, providerKeyType);
        var encryptedOrganizationKey = graph.WrapOrganizationKey(
            graph.ProviderOrganizationKey,
            graph.ProviderKey);
        var profile = graph.Profile(
            providers: [new ProfileProviderDto("provider-1", encryptedProviderKey)],
            providerOrganizations:
            [
                new ProfileProviderOrganizationDto(
                    "provider-org",
                    "provider-1",
                    encryptedOrganizationKey,
                    "Provider Org",
                    2,
                    1,
                    true),
            ]);
        var sync = graph.Sync(
            profile,
            ciphers: [graph.Cipher("provider-cipher", "provider-org", "Provider secret", graph.ProviderOrganizationKey)],
            collections:
            [
                new CollectionDto(
                    "provider-collection",
                    "provider-org",
                    graph.EncryptText("Provider collection", graph.ProviderOrganizationKey),
                    false,
                    false,
                    true),
            ]);

        var result = graph.Decrypt(sync);

        Assert.Equal("Provider secret", Assert.Single(result.Ciphers).Name);
        Assert.Equal("Provider collection", Assert.Single(result.Collections).Name);
        Assert.Equal(0, result.UnsupportedProviderOrganizationCount);
        Assert.Equal(0, result.UnsupportedOrganizationCipherCount);
        Assert.Equal(graph.EncryptedPrivateKey, result.WrappedAccountPrivateKey);
        var organization = Assert.Single(result.Organizations);
        Assert.Equal("provider-org", organization.Id);
        Assert.Equal("Provider Org", organization.Name);
        Assert.Equal(2, organization.Status);
        Assert.Equal(1, organization.Type);
        Assert.True(organization.Enabled);
        Assert.True(organization.IsProviderOrganization);
        Assert.False(organization.IsMember);
        Assert.True(organization.IsProviderUser);
        Assert.Equal("provider-1", organization.ProviderId);
        Assert.Equal(encryptedProviderKey, organization.EncryptedProviderKey);
        Assert.Equal(encryptedOrganizationKey, organization.EncryptedKey);
    }

    [Theory]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64)]
    public void Decrypt_HistoricalDirectOrganizationKeyTypes_DecryptCipher(
        EncryptionType organizationKeyType)
    {
        using var graph = new ProviderGraph();
        var encryptedOrganizationKey = graph.WrapProviderKey(
            graph.DirectOrganizationKey,
            organizationKeyType);
        var profile = graph.Profile(
            organizations:
            [
                new ProfileOrganizationDto(
                    "direct-org",
                    "Direct Org",
                    encryptedOrganizationKey,
                    2,
                    0,
                    true),
            ]);
        var sync = graph.Sync(
            profile,
            [graph.Cipher(
                "direct-cipher",
                "direct-org",
                "Direct secret",
                graph.DirectOrganizationKey)]);

        var result = graph.Decrypt(sync);

        Assert.Equal("Direct secret", Assert.Single(result.Ciphers).Name);
        Assert.Equal(0, result.UnsupportedOrganizationCipherCount);
    }

    [Fact]
    public void Decrypt_DirectOrganizationType2Key_HidesCipherAndCollection()
    {
        using var graph = new ProviderGraph();
        var profile = graph.Profile(
            organizations:
            [
                new ProfileOrganizationDto(
                    "direct-org",
                    "Direct Org",
                    graph.WrapOrganizationKey(graph.DirectOrganizationKey, graph.UserKey),
                    2,
                    0,
                    true),
            ]);

        var result = graph.Decrypt(graph.Sync(
            profile,
            [graph.Cipher(
                "direct-cipher",
                "direct-org",
                "Must stay hidden",
                graph.DirectOrganizationKey)],
            [new CollectionDto(
                "direct-collection",
                "direct-org",
                graph.EncryptText("Must stay hidden", graph.DirectOrganizationKey),
                false,
                false,
                true)]));

        Assert.Empty(result.Ciphers);
        Assert.Empty(result.Collections);
        Assert.Equal(1, result.UnsupportedOrganizationCipherCount);
    }

    [Fact]
    public void Decrypt_DirectOrganizationRsaWrapped32ByteKey_HidesCipherAndCollection()
    {
        using var graph = new ProviderGraph();
        using var shortOrganizationKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(32));
        var profile = graph.Profile(
            organizations:
            [
                new ProfileOrganizationDto(
                    "direct-org",
                    "Direct Org",
                    graph.WrapProviderKey(
                        shortOrganizationKey,
                        EncryptionType.Rsa2048_OaepSha1_B64),
                    2,
                    0,
                    true),
            ]);

        var result = graph.Decrypt(graph.Sync(
            profile,
            [graph.Cipher(
                "direct-cipher",
                "direct-org",
                "Must stay hidden",
                shortOrganizationKey)],
            [new CollectionDto(
                "direct-collection",
                "direct-org",
                graph.EncryptText("Must stay hidden", shortOrganizationKey),
                false,
                false,
                true)]));

        Assert.Empty(result.Ciphers);
        Assert.Empty(result.Collections);
        Assert.Equal(1, result.UnsupportedOrganizationCipherCount);
    }

    [Fact]
    public void Decrypt_MixedDirectAndProviderOrganizations_UsesTheirBoundKeys()
    {
        using var graph = new ProviderGraph();
        var profile = graph.Profile(
            organizations:
            [
                new ProfileOrganizationDto(
                    "direct-org",
                    "Direct",
                    graph.WrapProviderKey(graph.DirectOrganizationKey, EncryptionType.Rsa2048_OaepSha1_B64),
                    2,
                    0,
                    true,
                    "provider-1"),
            ],
            providers:
            [
                new ProfileProviderDto(
                    "provider-1",
                    graph.WrapProviderKey(graph.ProviderKey, EncryptionType.Rsa2048_OaepSha1_B64)),
            ],
            providerOrganizations:
            [
                new ProfileProviderOrganizationDto(
                    "provider-org",
                    "provider-1",
                    graph.WrapOrganizationKey(graph.ProviderOrganizationKey, graph.ProviderKey)),
            ]);
        var sync = graph.Sync(
            profile,
            ciphers:
            [
                graph.Cipher("direct-cipher", "direct-org", "Direct secret", graph.DirectOrganizationKey),
                graph.Cipher("provider-cipher", "provider-org", "Provider secret", graph.ProviderOrganizationKey),
            ],
            collections:
            [
                new CollectionDto(
                    "direct-collection",
                    "direct-org",
                    graph.EncryptText("Direct collection", graph.DirectOrganizationKey),
                    false,
                    false,
                    true),
                new CollectionDto(
                    "provider-collection",
                    "provider-org",
                    graph.EncryptText("Provider collection", graph.ProviderOrganizationKey),
                    false,
                    false,
                    true),
            ]);

        var result = graph.Decrypt(sync);

        Assert.Equal(["Direct secret", "Provider secret"], result.Ciphers.Select(cipher => cipher.Name));
        Assert.Equal(
            ["Direct collection", "Provider collection"],
            result.Collections.Select(collection => collection.Name));
        Assert.Equal(
            ["direct-org", "provider-org"],
            result.Organizations.Select(organization => organization.Id));
        Assert.Equal(0, result.UnsupportedProviderOrganizationCount);
    }

    [Fact]
    public void Decrypt_MissingProviderKey_DoesNotFallBackToUserKey()
    {
        using var graph = new ProviderGraph();
        var profile = graph.Profile(
            providerOrganizations:
            [
                new ProfileProviderOrganizationDto(
                    "provider-org",
                    "missing-provider",
                    graph.WrapOrganizationKey(graph.ProviderOrganizationKey, graph.UserKey)),
            ]);

        var result = graph.Decrypt(graph.Sync(
            profile,
            [graph.Cipher("cipher", "provider-org", "Must stay hidden", graph.ProviderOrganizationKey)]));

        Assert.Empty(result.Ciphers);
        Assert.Equal(1, result.UnsupportedProviderOrganizationCount);
        Assert.Equal(1, result.UnsupportedOrganizationCipherCount);
    }

    [Fact]
    public void Decrypt_WrongLengthProviderKey_FailsClosed()
    {
        using var graph = new ProviderGraph();
        using var shortProviderKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(32));
        var profile = graph.Profile(
            providers:
            [
                new ProfileProviderDto(
                    "provider-1",
                    graph.WrapProviderKey(shortProviderKey, EncryptionType.Rsa2048_OaepSha1_B64)),
            ],
            providerOrganizations:
            [
                new ProfileProviderOrganizationDto(
                    "provider-org",
                    "provider-1",
                    graph.WrapOrganizationKey(graph.ProviderOrganizationKey, graph.ProviderKey)),
            ]);

        var result = graph.Decrypt(graph.Sync(profile));

        Assert.Equal(1, result.UnsupportedProviderOrganizationCount);
    }

    [Fact]
    public void Decrypt_Legacy32ByteProviderOrganizationKey_IsSupported()
    {
        using var graph = new ProviderGraph();
        using var organizationKey = new SymmetricCryptoKey(
            Enumerable.Range(0, 32).Select(index => (byte)(0xA0 + index)).ToArray());
        var profile = graph.Profile(
            providers:
            [
                new ProfileProviderDto(
                    "provider-1",
                    graph.WrapProviderKey(graph.ProviderKey, EncryptionType.Rsa2048_OaepSha1_B64)),
            ],
            providerOrganizations:
            [
                new ProfileProviderOrganizationDto(
                    "provider-org",
                    "provider-1",
                    graph.WrapOrganizationKey(organizationKey, graph.ProviderKey),
                    "Legacy Provider Org",
                    2,
                    1,
                    true),
            ]);

        var result = graph.Decrypt(graph.Sync(
            profile,
            collections:
            [
                new CollectionDto(
                    "provider-collection",
                    "provider-org",
                    graph.EncryptText("Legacy collection", organizationKey),
                    false,
                    false,
                    true),
            ]));

        Assert.Equal("Legacy collection", Assert.Single(result.Collections).Name);
        Assert.Equal(0, result.UnsupportedProviderOrganizationCount);
    }

    [Fact]
    public void Decrypt_DuplicateProviderIds_FailsClosed()
    {
        using var graph = new ProviderGraph();
        var wrappedProviderKey = graph.WrapProviderKey(
            graph.ProviderKey,
            EncryptionType.Rsa2048_OaepSha1_B64);
        var profile = graph.Profile(
            providers:
            [
                new ProfileProviderDto("provider-1", wrappedProviderKey),
                new ProfileProviderDto("provider-1", wrappedProviderKey),
            ],
            providerOrganizations:
            [
                new ProfileProviderOrganizationDto(
                    "provider-org",
                    "provider-1",
                    graph.WrapOrganizationKey(graph.ProviderOrganizationKey, graph.ProviderKey)),
            ]);

        var result = graph.Decrypt(graph.Sync(profile));

        Assert.Equal(1, result.UnsupportedProviderOrganizationCount);
    }

    [Fact]
    public void Decrypt_DuplicateProviderOrganizationIds_FailsClosed()
    {
        using var graph = new ProviderGraph();
        var wrappedOrganizationKey = graph.WrapOrganizationKey(
            graph.ProviderOrganizationKey,
            graph.ProviderKey);
        var profile = graph.Profile(
            providers:
            [
                new ProfileProviderDto(
                    "provider-1",
                    graph.WrapProviderKey(graph.ProviderKey, EncryptionType.Rsa2048_OaepSha1_B64)),
            ],
            providerOrganizations:
            [
                new ProfileProviderOrganizationDto("provider-org", "provider-1", wrappedOrganizationKey),
                new ProfileProviderOrganizationDto("provider-org", "provider-1", wrappedOrganizationKey),
            ]);

        var result = graph.Decrypt(graph.Sync(profile));

        Assert.Equal(1, result.UnsupportedProviderOrganizationCount);
    }

    [Fact]
    public void Decrypt_CrossProviderCiphertextReplacement_FailsClosed()
    {
        using var graph = new ProviderGraph();
        var profile = graph.Profile(
            providers:
            [
                new ProfileProviderDto(
                    "provider-a",
                    graph.WrapProviderKey(graph.ProviderKey, EncryptionType.Rsa2048_OaepSha1_B64)),
                new ProfileProviderDto(
                    "provider-b",
                    graph.WrapProviderKey(graph.SecondProviderKey, EncryptionType.Rsa2048_OaepSha1_B64)),
            ],
            providerOrganizations:
            [
                new ProfileProviderOrganizationDto(
                    "provider-org",
                    "provider-b",
                    graph.WrapOrganizationKey(graph.ProviderOrganizationKey, graph.ProviderKey)),
            ]);

        var result = graph.Decrypt(graph.Sync(profile));

        Assert.Equal(1, result.UnsupportedProviderOrganizationCount);
    }

    [Fact]
    public void Decrypt_DirectAndProviderOrganizationSameId_ProviderKeyOverridesDirectKey()
    {
        using var graph = new ProviderGraph();
        var profile = graph.Profile(
            organizations:
            [
                new ProfileOrganizationDto(
                    "shared-org",
                    "Direct",
                    graph.WrapProviderKey(graph.DirectOrganizationKey, EncryptionType.Rsa2048_OaepSha1_B64),
                    2,
                    0),
            ],
            providers:
            [
                new ProfileProviderDto(
                    "provider-1",
                    graph.WrapProviderKey(graph.ProviderKey, EncryptionType.Rsa2048_OaepSha1_B64)),
            ],
            providerOrganizations:
            [
                new ProfileProviderOrganizationDto(
                    "shared-org",
                    "provider-1",
                    graph.WrapOrganizationKey(graph.ProviderOrganizationKey, graph.ProviderKey),
                    "Provider Override",
                    2,
                    1,
                    false),
            ]);

        var result = graph.Decrypt(graph.Sync(
            profile,
            [graph.Cipher("cipher", "shared-org", "Provider secret", graph.ProviderOrganizationKey)],
            [
                new CollectionDto(
                    "collection",
                    "shared-org",
                    graph.EncryptText("Provider collection", graph.ProviderOrganizationKey),
                    false,
                    false,
                    true),
            ]));

        Assert.Equal("Provider secret", Assert.Single(result.Ciphers).Name);
        Assert.Equal("Provider collection", Assert.Single(result.Collections).Name);
        Assert.Equal(0, result.UnsupportedProviderOrganizationCount);
        Assert.Equal(0, result.UnsupportedOrganizationCipherCount);
        var organization = Assert.Single(result.Organizations);
        Assert.Equal("Direct", organization.Name);
        Assert.Equal(0, organization.Type);
        Assert.True(organization.Enabled);
        Assert.True(organization.IsProviderOrganization);
        Assert.True(organization.IsMember);
        Assert.True(organization.IsProviderUser);
        Assert.Equal("provider-1", organization.ProviderId);
        Assert.NotNull(organization.EncryptedProviderKey);
    }

    [Fact]
    public void Decrypt_InvalidProviderOrganization_RemovesDirectKeyFallback()
    {
        using var graph = new ProviderGraph();
        var profile = graph.Profile(
            organizations:
            [
                new ProfileOrganizationDto(
                    "shared-org",
                    "Direct",
                    graph.WrapProviderKey(graph.DirectOrganizationKey, EncryptionType.Rsa2048_OaepSha1_B64),
                    2,
                    0),
            ],
            providerOrganizations:
            [
                new ProfileProviderOrganizationDto(
                    "shared-org",
                    "missing-provider",
                    graph.WrapOrganizationKey(graph.ProviderOrganizationKey, graph.ProviderKey)),
            ]);

        var result = graph.Decrypt(graph.Sync(
            profile,
            [graph.Cipher("cipher", "shared-org", "Must stay hidden", graph.DirectOrganizationKey)]));

        Assert.Empty(result.Ciphers);
        Assert.Equal(1, result.UnsupportedProviderOrganizationCount);
        Assert.Equal(1, result.UnsupportedOrganizationCipherCount);
    }

    [Fact]
    public void Decrypt_V2AccountKeys_ReportsProviderOrganizationUnsupported()
    {
        using var graph = new ProviderGraph();
        using var document = JsonDocument.Parse("""
        {
          "publicKeyEncryptionKeyPair": { "wrappedPrivateKey": "7.cose" },
          "signatureKeyPair": { "wrappedSigningKey": "7.signing", "verifyingKey": "cose-key" },
          "securityState": { "securityState": "cose-sign1", "securityVersion": 2 }
        }
        """);
        var profile = graph.Profile(
            providers:
            [
                new ProfileProviderDto(
                    "provider-1",
                    graph.WrapProviderKey(graph.ProviderKey, EncryptionType.Rsa2048_OaepSha1_B64)),
            ],
            providerOrganizations:
            [
                new ProfileProviderOrganizationDto(
                    "provider-org",
                    "provider-1",
                    graph.WrapOrganizationKey(graph.ProviderOrganizationKey, graph.ProviderKey)),
            ]) with
        {
            AccountKeys = document.RootElement.Clone(),
        };

        var result = graph.Decrypt(graph.Sync(profile));

        Assert.Equal(1, result.UnsupportedProviderOrganizationCount);
        Assert.Null(result.WrappedAccountPrivateKey);
    }

    [Fact]
    public void Decrypt_V1AccountKeys_DecryptsProviderOrganization()
    {
        using var graph = new ProviderGraph();
        using var document = JsonDocument.Parse($$"""
        {
          "publicKeyEncryptionKeyPair": {
            "wrappedPrivateKey": {{JsonSerializer.Serialize(graph.EncryptedPrivateKey)}},
            "publicKey": "v1-public-key"
          },
          "signatureKeyPair": null,
          "securityState": null
        }
        """);
        var profile = graph.Profile(
            providers:
            [
                new ProfileProviderDto(
                    "provider-1",
                    graph.WrapProviderKey(graph.ProviderKey, EncryptionType.Rsa2048_OaepSha1_B64)),
            ],
            providerOrganizations:
            [
                new ProfileProviderOrganizationDto(
                    "provider-org",
                    "provider-1",
                    graph.WrapOrganizationKey(graph.ProviderOrganizationKey, graph.ProviderKey)),
            ]) with
        {
            AccountKeys = document.RootElement.Clone(),
            PrivateKey = null,
        };

        var result = graph.Decrypt(graph.Sync(profile));

        Assert.Equal(0, result.UnsupportedProviderOrganizationCount);
        Assert.Equal(graph.EncryptedPrivateKey, result.WrappedAccountPrivateKey);
    }

    [Theory]
    [InlineData(32, EncryptionType.Rsa2048_OaepSha256_B64)]
    [InlineData(64, EncryptionType.Rsa2048_OaepSha1_B64)]
    [InlineData(32, EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64)]
    [InlineData(64, EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64)]
    public void OrganizationKeyResolver_ProviderOrganization_UsesCanonicalThreeStepChain(
        int organizationKeyLength,
        EncryptionType providerKeyType)
    {
        using var graph = new ProviderGraph();
        using var organizationKey = new SymmetricCryptoKey(
            Enumerable.Range(0, organizationKeyLength).Select(index => (byte)(0xA0 + index)).ToArray());
        var encryptedProviderKey = graph.WrapProviderKey(
            graph.ProviderKey,
            providerKeyType);
        var organization = new Core.Models.VaultOrganization(
            "provider-org",
            "Provider Org",
            2,
            1,
            true)
        {
            EncryptedKey = graph.WrapOrganizationKey(organizationKey, graph.ProviderKey),
            ProviderId = "provider-1",
            EncryptedProviderKey = encryptedProviderKey,
            IsProviderOrganization = true,
        };

        using var resolved = new OrganizationKeyResolver(new CryptoService()).Resolve(
            organization,
            graph.EncryptedPrivateKey,
            graph.UserKey);

        Assert.Equal(organizationKey.FullKey, resolved.FullKey);
    }

    [Theory]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64)]
    public void OrganizationKeyResolver_DirectOrganization_AcceptsHistoricalRsaTypes(
        EncryptionType organizationKeyType)
    {
        using var graph = new ProviderGraph();
        var organization = new Core.Models.VaultOrganization(
            "direct-org",
            "Direct Org",
            2,
            0,
            true)
        {
            EncryptedKey = graph.WrapProviderKey(
                graph.DirectOrganizationKey,
                organizationKeyType),
        };

        using var resolved = new OrganizationKeyResolver(new CryptoService()).Resolve(
            organization,
            graph.EncryptedPrivateKey,
            graph.UserKey);

        Assert.Equal(graph.DirectOrganizationKey.FullKey, resolved.FullKey);
    }

    [Fact]
    public void OrganizationKeyResolver_DirectOrganization_RejectsType2Envelope()
    {
        using var graph = new ProviderGraph();
        var organization = new Core.Models.VaultOrganization(
            "direct-org",
            "Direct Org",
            2,
            0,
            true)
        {
            EncryptedKey = graph.WrapOrganizationKey(graph.DirectOrganizationKey, graph.UserKey),
        };

        Assert.Throws<Core.Services.OrganizationCipherOperationException>(() =>
            new OrganizationKeyResolver(new CryptoService()).Resolve(
                organization,
                graph.EncryptedPrivateKey,
                graph.UserKey));
    }

    [Fact]
    public void OrganizationKeyResolver_DirectOrganization_RejectsRsaWrapped32ByteKey()
    {
        using var graph = new ProviderGraph();
        using var shortOrganizationKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(32));
        var organization = new Core.Models.VaultOrganization(
            "direct-org",
            "Direct Org",
            2,
            0,
            true)
        {
            EncryptedKey = graph.WrapProviderKey(
                shortOrganizationKey,
                EncryptionType.Rsa2048_OaepSha1_B64),
        };

        Assert.Throws<Core.Services.OrganizationCipherOperationException>(() =>
            new OrganizationKeyResolver(new CryptoService()).Resolve(
                organization,
                graph.EncryptedPrivateKey,
                graph.UserKey));
    }

    [Fact]
    public void OrganizationKeyResolver_ProviderOrganization_NeverFallsBackToUserKey()
    {
        using var graph = new ProviderGraph();
        var organization = new Core.Models.VaultOrganization(
            "provider-org",
            "Provider Org",
            2,
            1,
            true)
        {
            EncryptedKey = graph.WrapOrganizationKey(
                graph.ProviderOrganizationKey,
                graph.UserKey),
            ProviderId = "provider-1",
            IsProviderOrganization = true,
        };

        Assert.Throws<Core.Services.OrganizationCipherOperationException>(() =>
            new OrganizationKeyResolver(new CryptoService()).Resolve(
                organization,
                graph.EncryptedPrivateKey,
                graph.UserKey));
    }

    [Fact]
    public void Decrypt_DuplicateDirectOrganizationIds_FailsClosed()
    {
        using var graph = new ProviderGraph();
        var wrappedOrganizationKey = graph.WrapProviderKey(
            graph.DirectOrganizationKey,
            EncryptionType.Rsa2048_OaepSha1_B64);
        var profile = graph.Profile(
            organizations:
            [
                new ProfileOrganizationDto("direct-org", "First", wrappedOrganizationKey, 2, 0),
                new ProfileOrganizationDto("direct-org", "Second", wrappedOrganizationKey, 2, 0),
            ]);

        var result = graph.Decrypt(graph.Sync(
            profile,
            [graph.Cipher("cipher", "direct-org", "Must stay hidden", graph.DirectOrganizationKey)]));

        Assert.Empty(result.Ciphers);
        Assert.Equal(1, result.UnsupportedOrganizationCipherCount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"publicKeyEncryptionKeyPair\":{\"wrappedPrivateKey\":\"7.cose\",\"publicKey\":\"key\"}}")]
    [InlineData("{\"publicKeyEncryptionKeyPair\":{\"wrappedPrivateKey\":\"2.invalid\"}}")]
    [InlineData("{\"signatureKeyPair\":null,\"SignatureKeyPair\":{},\"publicKeyEncryptionKeyPair\":{\"wrappedPrivateKey\":\"2.AA==|AA==|AA==\",\"publicKey\":\"key\"}}")]
    [InlineData("{\"publicKeyEncryptionKeyPair\":{\"wrappedPrivateKey\":\"2.AA==|AA==|AA==\",\"publicKey\":\"key\"},\"PublicKeyEncryptionKeyPair\":{\"wrappedPrivateKey\":\"2.AA==|AA==|AA==\",\"publicKey\":\"other\"}}")]
    public void Decrypt_UnknownOrMalformedAccountKeys_FailsClosed(string accountKeys)
    {
        using var graph = new ProviderGraph();
        using var document = JsonDocument.Parse(accountKeys);
        var profile = graph.Profile(
            providers:
            [
                new ProfileProviderDto(
                    "provider-1",
                    graph.WrapProviderKey(graph.ProviderKey, EncryptionType.Rsa2048_OaepSha1_B64)),
            ],
            providerOrganizations:
            [
                new ProfileProviderOrganizationDto(
                    "provider-org",
                    "provider-1",
                    graph.WrapOrganizationKey(graph.ProviderOrganizationKey, graph.ProviderKey)),
            ]) with
        {
            AccountKeys = document.RootElement.Clone(),
        };

        var result = graph.Decrypt(graph.Sync(profile));

        Assert.Equal(1, result.UnsupportedProviderOrganizationCount);
    }

    private sealed class ProviderGraph : IDisposable
    {
        private readonly CryptoService _crypto = new();
        private readonly RSA _accountRsa = RSA.Create(2048);

        public ProviderGraph()
        {
            UserKey = Key(0x00);
            ProviderKey = Key(0x40);
            SecondProviderKey = Key(0x80);
            ProviderOrganizationKey = Key(0xC0);
            DirectOrganizationKey = Key(0x20);

            var privateKey = _accountRsa.ExportPkcs8PrivateKey();
            try
            {
                EncryptedPrivateKey = _crypto.Encrypt(privateKey, UserKey).ToString();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }

        public SymmetricCryptoKey UserKey { get; }
        public SymmetricCryptoKey ProviderKey { get; }
        public SymmetricCryptoKey SecondProviderKey { get; }
        public SymmetricCryptoKey ProviderOrganizationKey { get; }
        public SymmetricCryptoKey DirectOrganizationKey { get; }
        public string EncryptedPrivateKey { get; }

        public ProfileDto Profile(
            ProfileOrganizationDto[]? organizations = null,
            ProfileProviderDto[]? providers = null,
            ProfileProviderOrganizationDto[]? providerOrganizations = null) =>
            new("user-1", "provider@example.com", "Provider User", null, EncryptedPrivateKey, organizations)
            {
                Providers = providers,
                ProviderOrganizations = providerOrganizations,
            };

        public SyncResponse Sync(
            ProfileDto profile,
            CipherDto[]? ciphers = null,
            CollectionDto[]? collections = null) =>
            new("sync", profile, [], ciphers ?? [], collections ?? [], []);

        public DecryptedVault Decrypt(SyncResponse sync) =>
            new VaultDecryptor(_crypto).Decrypt(sync, UserKey, "https://vault.example");

        public string WrapProviderKey(SymmetricCryptoKey key, EncryptionType type)
        {
            var padding = type switch
            {
                EncryptionType.Rsa2048_OaepSha1_B64
                    or EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64 =>
                    RSAEncryptionPadding.OaepSHA1,
                EncryptionType.Rsa2048_OaepSha256_B64
                    or EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64 =>
                    RSAEncryptionPadding.OaepSHA256,
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
            var ciphertext = _accountRsa.Encrypt(key.FullKey, padding);
            return new EncString(
                type,
                [],
                ciphertext,
                type.HasLegacyRsaMac()
                    ? new byte[EncString.LegacyRsaMacLength]
                    : null).ToString();
        }

        public string WrapOrganizationKey(
            SymmetricCryptoKey organizationKey,
            SymmetricCryptoKey providerKey) =>
            _crypto.Encrypt(organizationKey.FullKey, providerKey).ToString();

        public string EncryptText(string value, SymmetricCryptoKey key) =>
            _crypto.Encrypt(Encoding.UTF8.GetBytes(value), key).ToString();

        public CipherDto Cipher(
            string id,
            string organizationId,
            string name,
            SymmetricCryptoKey key) =>
            new(
                id,
                (int)CipherType.Login,
                EncryptText(name, key),
                null,
                null,
                organizationId,
                null,
                false,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                null);

        public void Dispose()
        {
            DirectOrganizationKey.Dispose();
            ProviderOrganizationKey.Dispose();
            SecondProviderKey.Dispose();
            ProviderKey.Dispose();
            UserKey.Dispose();
            _accountRsa.Dispose();
        }

        private static SymmetricCryptoKey Key(byte start) =>
            new(Enumerable.Range(0, 64).Select(index => unchecked((byte)(start + index))).ToArray());
    }
}
