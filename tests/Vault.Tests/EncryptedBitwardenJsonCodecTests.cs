using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Crypto.V2;
using Vault.Porting;
using Xunit;

namespace Vault.Tests;

public sealed class EncryptedBitwardenJsonCodecTests
{
    private const string Password = "portable-test-password";
    private const string EmptyPlaintext = "{\"encrypted\":false,\"folders\":[],\"items\":[]}";

    [Fact]
    public void UnauthenticatedLegacyKeyCannotProduceAnUnrestorableRestrictedBackup()
    {
        using var key = VaultUserKey.TakeOwnership(new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(32)));

        Assert.Throws<NotSupportedException>(() => EncryptedBitwardenJsonCodec.ExportAccountRestricted(
            [Login()], [], key, VaultCapabilities.None));
    }

    // Independent Node.js crypto fixture: PBKDF2-SHA256 over UTF8(base64 salt),
    // HKDF-Expand('enc'/'mac'), AES-256-CBC and HMAC-SHA256(IV || ciphertext).
    // The historical 5,000-iteration setting is accepted for reading, never newly written.
    private const string IndependentPbkdf2Fixture = """
        {"encrypted":true,"passwordProtected":true,"salt":"AAECAwQFBgcICQoLDA0ODw==","kdfType":0,"kdfIterations":5000,"encKeyValidation_DO_NOT_EDIT":"2.AQEBAQEBAQEBAQEBAQEBAQ==|UC18KwEuKhXZiEgjG9ixZHLfSrBbDLaOV83q3ihhBnkDFLpGnPpotmw/E7oaer5B|P+TjRJtCahDaN2oGzr2wXZNT0hOJ1UErZhoR9VxWdbg=","data":"2.AgICAgICAgICAgICAgICAg==|kyuY9pIE+273siW6taHHhpV2CdhN9oc5Fh+IZye5xhHqVJPrbyjmskajxN2EaQ+B|TJVeWWjjwYPRV3ag89BdtBndxP2vgVmC/ggR1EjfEs4="}
        """;

    [Fact]
    public void ReadsIndependentPbkdf2Fixture()
    {
        Assert.Equal(ImportProtection.Password, EncryptedBitwardenJsonCodec.ReadProtection(IndependentPbkdf2Fixture));
        Assert.Equal(EmptyPlaintext, EncryptedBitwardenJsonCodec.DecryptWithPassword(IndependentPbkdf2Fixture, Password));
    }

    [Theory]
    [InlineData(KdfType.Pbkdf2, 600_000, null, null)]
    [InlineData(KdfType.Argon2id, 2, 16, 1)]
    public void PasswordRoundTripProtectsWholePayload(KdfType type, int iterations, int? memory, int? parallelism)
    {
        var plain = BitwardenJsonCodec.Serialize([Login()], [new Folder { Id = "f1", Name = "Private work" }]);
        var protectedJson = EncryptedBitwardenJsonCodec.EncryptWithPassword(
            plain, Password, new EncryptedBackupKdfOptions(type, iterations, memory, parallelism));

        Assert.DoesNotContain("private-password-value", protectedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Private work", protectedJson, StringComparison.Ordinal);
        Assert.Equal(plain, EncryptedBitwardenJsonCodec.DecryptWithPassword(protectedJson, Password));
        using var root = JsonDocument.Parse(protectedJson);
        Assert.Equal((int)type, root.RootElement.GetProperty("kdfType").GetInt32());
        Assert.Equal(iterations, root.RootElement.GetProperty("kdfIterations").GetInt32());
        Assert.Equal(16, Convert.FromBase64String(root.RootElement.GetProperty("salt").GetString()!).Length);
    }

    [Fact]
    public void ExportUsesFreshSaltAndCiphertext()
    {
        var first = EncryptedBitwardenJsonCodec.EncryptWithPassword(EmptyPlaintext, Password);
        var second = EncryptedBitwardenJsonCodec.EncryptWithPassword(EmptyPlaintext, Password);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void WrongPasswordIsRejected()
    {
        Assert.Throws<CryptographicException>(() =>
            EncryptedBitwardenJsonCodec.DecryptWithPassword(IndependentPbkdf2Fixture, "incorrect-password"));
    }

    [Theory]
    [InlineData("data")]
    [InlineData("encKeyValidation_DO_NOT_EDIT")]
    public void ModifiedAuthenticatedCiphertextIsRejected(string property)
    {
        var modified = JsonNode.Parse(IndependentPbkdf2Fixture)!.AsObject();
        var original = modified[property]!.GetValue<string>();
        var segments = original.Split('|');
        var ciphertext = Convert.FromBase64String(segments[1]);
        ciphertext[0] ^= 0x80;
        segments[1] = Convert.ToBase64String(ciphertext);
        modified[property] = string.Join('|', segments);

        Assert.Throws<CryptographicException>(() =>
            EncryptedBitwardenJsonCodec.DecryptWithPassword(modified.ToJsonString(), Password));
    }

    [Fact]
    public void UnauthenticatedCiphertextDowngradeIsRejected()
    {
        var modified = JsonNode.Parse(IndependentPbkdf2Fixture)!.AsObject();
        modified["data"] = "0.AgICAgICAgICAgICAgICAg==|kyuY9pIE+273siW6taHHhpV2CdhN9oc5Fh+IZye5xhHqVJPrbyjmskajxN2EaQ+B";
        Assert.Throws<CryptographicException>(() =>
            EncryptedBitwardenJsonCodec.DecryptWithPassword(modified.ToJsonString(), Password));
    }

    [Theory]
    [InlineData(0, 0, null, null)]
    [InlineData(0, 2_000_001, null, null)]
    [InlineData(1, 2, 1_025, 1)]
    [InlineData(1, 2, 16, 17)]
    [InlineData(1, 1, 16, 1)]
    [InlineData(99, 600_000, null, null)]
    public void UntrustedKdfParametersAreRejectedBeforeDerivation(int type, int iterations, int? memory, int? parallelism)
    {
        var modified = JsonNode.Parse(IndependentPbkdf2Fixture)!.AsObject();
        modified["kdfType"] = type;
        modified["kdfIterations"] = iterations;
        modified["kdfMemory"] = memory;
        modified["kdfParallelism"] = parallelism;

        Assert.Throws<InvalidDataException>(() =>
            EncryptedBitwardenJsonCodec.DecryptWithPassword(modified.ToJsonString(), Password));
    }

    [Fact]
    public void NewlyWrittenPbkdf2BackupCannotUseHistoricalWeakSetting()
    {
        Assert.Throws<InvalidDataException>(() => EncryptedBitwardenJsonCodec.EncryptWithPassword(
            EmptyPlaintext, Password, new EncryptedBackupKdfOptions(KdfType.Pbkdf2, 5_000)));
    }

    [Fact]
    public void DuplicateEncryptionMetadataIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => EncryptedBitwardenJsonCodec.ReadProtection(
            "{\"encrypted\":false,\"Encrypted\":true,\"folders\":[],\"items\":[]}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AccountRestrictedRoundTripRequiresSameAccountKey(bool v2)
    {
        using var key = NewUserKey(v2);
        var json = EncryptedBitwardenJsonCodec.ExportAccountRestricted(
            [Login()], [new Folder { Id = "f1", Name = "Private work" }], key, VaultCapabilities.None);
        Assert.Equal(ImportProtection.Account, EncryptedBitwardenJsonCodec.ReadProtection(json));
        Assert.DoesNotContain("private-password-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Private work", json, StringComparison.Ordinal);
        var plaintext = EncryptedBitwardenJsonCodec.DecryptAccountRestricted(json, key);
        var parsed = BitwardenJsonCodec.Parse(plaintext);
        Assert.Equal("private-password-value", Assert.Single(parsed.Ciphers).Login!.Password);
        Assert.Equal("Private work", Assert.Single(parsed.Folders).Name);
        Assert.Equal((0, 0), Assert.Single(parsed.Relations));

        using var unrelatedKey = NewUserKey(v2);
        Assert.ThrowsAny<CryptographicException>(() =>
            EncryptedBitwardenJsonCodec.DecryptAccountRestricted(json, unrelatedKey));
    }

    [Fact]
    public void AccountRestrictedCorruptItemRejectsEntireDocument()
    {
        using var key = NewUserKey(v2: false);
        var json = EncryptedBitwardenJsonCodec.ExportAccountRestricted(
            [Login()], [], key, VaultCapabilities.None);
        var modified = JsonNode.Parse(json)!.AsObject();
        modified["items"]![0]!["name"] = "2.invalid|invalid|invalid";
        Assert.Throws<FormatException>(() => EncryptedBitwardenJsonCodec.DecryptAccountRestricted(modified.ToJsonString(), key));
    }

    [Fact]
    public void AccountRestrictedRejectsOrganizationItems()
    {
        using var key = NewUserKey(v2: false);
        var organizationCipher = new Cipher
        {
            Type = CipherType.Login,
            Name = "Shared",
            OrganizationId = "organization",
            Login = new CipherLogin("user", "secret", null, []),
        };
        Assert.Throws<InvalidDataException>(() => EncryptedBitwardenJsonCodec.ExportAccountRestricted(
            [organizationCipher], [], key, VaultCapabilities.None));
    }

    [Theory]
    [InlineData("folder")]
    [InlineData("name")]
    [InlineData("password")]
    [InlineData("key")]
    public void AccountRestrictedRejectsAuthenticatedFieldDowngrade(string target)
    {
        using var key = NewUserKey(v2: false);
        var json = EncryptedBitwardenJsonCodec.ExportAccountRestricted(
            [Login()], [new Folder { Id = "f1", Name = "Private work" }], key, VaultCapabilities.None);
        var modified = JsonNode.Parse(json)!.AsObject();
        var owner = target switch
        {
            "folder" => modified["folders"]![0]!,
            "password" => modified["items"]![0]!["login"]!,
            _ => modified["items"]![0]!,
        };
        var property = target == "folder" ? "name" : target;
        var original = owner[property]!.GetValue<string>();
        var parts = original[2..].Split('|');
        var iv = Convert.FromBase64String(parts[0]);
        iv[0] ^= 1;
        owner[property] = "0." + Convert.ToBase64String(iv) + "|" + parts[1];

        Assert.Throws<CryptographicException>(() =>
            EncryptedBitwardenJsonCodec.DecryptAccountRestricted(modified.ToJsonString(), key));
    }

    [Fact]
    public void AccountRestrictedRejectsV2DataDowngrade()
    {
        using var key = NewUserKey(v2: true);
        var json = EncryptedBitwardenJsonCodec.ExportAccountRestricted([Login()], [], key, VaultCapabilities.None);
        var modified = JsonNode.Parse(json)!.AsObject();
        modified["items"]![0]!["data"] = "0.untrusted|ciphertext";
        Assert.Throws<CryptographicException>(() =>
            EncryptedBitwardenJsonCodec.DecryptAccountRestricted(modified.ToJsonString(), key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AccountRestrictedFieldCasingCannotBypassAuthentication(bool nested)
    {
        using var key = NewUserKey(v2: false);
        var json = EncryptedBitwardenJsonCodec.ExportAccountRestricted([Login()], [], key, VaultCapabilities.None);
        var modified = JsonNode.Parse(json)!.AsObject();
        var item = modified["items"]![0]!.AsObject();
        var owner = nested ? item["login"]!.AsObject() : item;
        var name = nested ? "password" : "name";
        var parts = owner[name]!.GetValue<string>()[2..].Split('|');
        owner[name] = "0." + parts[0] + "|" + parts[1];
        if (nested)
        {
            var value = item["login"];
            item.Remove("login");
            item["Login"] = value;
        }
        else
        {
            var value = item["name"];
            item.Remove("name");
            item["Name"] = value;
        }
        Assert.Throws<CryptographicException>(() =>
            EncryptedBitwardenJsonCodec.DecryptAccountRestricted(modified.ToJsonString(), key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AccountBackupCreatesIndependentKeysForExistingBlobItems(bool v2)
    {
        using var key = NewUserKey(v2);
        var source = new Cipher
        {
            Id = "original-item-id",
            Key = "source-wrapper-is-not-reused",
            UsesBlobEncryption = true,
            Type = CipherType.Login,
            Name = "Original blob name",
            Login = new CipherLogin("user", "secret-blob-password", null, []),
        };
        var json = EncryptedBitwardenJsonCodec.ExportAccountRestricted([source], [], key, VaultCapabilities.None);
        Assert.DoesNotContain("source-wrapper-is-not-reused", json, StringComparison.Ordinal);
        var restored = Assert.Single(BitwardenJsonCodec.Parse(
            EncryptedBitwardenJsonCodec.DecryptAccountRestricted(json, key)).Ciphers);
        Assert.Equal("original-item-id", restored.Id);
        Assert.Equal("secret-blob-password", restored.Login!.Password);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AccountBackupDoesNotRequireServerWriteCapabilities(bool v2)
    {
        using var key = NewUserKey(v2);
        var archivedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var source = new Cipher
        {
            Id = "bank-source",
            Type = CipherType.BankAccount,
            Name = "Personal bank",
            ArchivedDate = archivedAt,
            BankAccount = new CipherBankAccount("Private bank", null, null, "123456", null, null, null, null, null, null),
        };
        var json = EncryptedBitwardenJsonCodec.ExportAccountRestricted([source], [], key, VaultCapabilities.None);
        var restored = Assert.Single(BitwardenJsonCodec.Parse(
            EncryptedBitwardenJsonCodec.DecryptAccountRestricted(json, key)).Ciphers);
        Assert.Equal("123456", restored.BankAccount!.AccountNumber);
        Assert.Equal(archivedAt, restored.ArchivedDate);
    }

    [Fact]
    public void MaximumContentLimitIsAppliedBeforeJsonParsing()
    {
        Assert.Throws<InvalidDataException>(() =>
            EncryptedBitwardenJsonCodec.ReadProtection(new string(' ', VaultPortingLimits.MaximumContentCharacters + 1)));
    }

    private static VaultUserKey NewUserKey(bool v2) => v2
        ? VaultUserKey.TakeOwnership(V2UserKey.Generate())
        : VaultUserKey.TakeOwnership(new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64)));

    private static Cipher Login() => new()
    {
        Type = CipherType.Login,
        Name = "Private item",
        FolderId = "f1",
        Login = new CipherLogin("private-user", "private-password-value", null, []),
        Notes = "Private notes",
    };
}
