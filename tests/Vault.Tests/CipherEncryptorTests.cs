using System.Security.Cryptography;
using System.Text.Json;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Passkeys;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class CipherEncryptorTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly CryptoService _crypto = new();
    private readonly SymmetricCryptoKey _userKey = new(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
    private CipherEncryptor NewEncryptor() => new(_crypto);

    // Encrypt a domain cipher, push it across the wire shape, and decrypt it back.
    private Cipher RoundTrip(Cipher source, VaultCapabilities? capabilities = null)
    {
        var request = capabilities is null
            ? NewEncryptor().Encrypt(source, _userKey)
            : NewEncryptor().Encrypt(source, _userKey, capabilities);
        var json = JsonSerializer.Serialize(request, Web);
        var dto = JsonSerializer.Deserialize<CipherDto>(json, Web)!;
        var sync = new SyncResponse("sync", new ProfileDto("u", "me@x", "Me", null, null), [], [dto]);
        return Assert.Single(new VaultDecryptor(_crypto).Decrypt(sync, _userKey, "https://x").Ciphers);
    }

    private static Cipher LoginSource() => new()
    {
        Type = CipherType.Login,
        Name = "GitHub",
        Notes = "note",
        FolderId = "f1",
        Favorite = true,
        Reprompt = true,
        Login = new CipherLogin("octo", "secret", "totp-seed",
            [new CipherLoginUri("https://github.com", 0)])
        {
            Fido2Credentials =
            [
                new CipherFido2Credential("AQIDBA", "public-key", "ECDSA", "P-256", "priv-val",
                    "github.com", "user-handle", "octo@x", 42, "GitHub", "Octo", true,
                    DateTimeOffset.Parse("2026-06-24T00:00:00Z")),
            ],
        },
        Fields =
        [
            new CipherField("Plain", "v1", CipherFieldType.Text),
            new CipherField("Secret", "v2", CipherFieldType.Hidden),
        ],
    };

    private static Cipher NewCipherTypeSource(CipherType type) => type switch
    {
        CipherType.BankAccount => new Cipher
        {
            Type = type,
            Name = "Bank",
            BankAccount = new CipherBankAccount(
                "Example", "Ada", "Checking", "123", "456", "001", "7890", "SWIFT", "IBAN", "555"),
        },
        CipherType.DriversLicense => new Cipher
        {
            Type = type,
            Name = "License",
            DriversLicense = new CipherDriversLicense(
                "Ada", "M", "Lovelace", "1815", "DL-1", "GB", "London", "2025", "2030", "DVLA", "B"),
        },
        CipherType.Passport => new Cipher
        {
            Type = type,
            Name = "Passport",
            Passport = new CipherPassport(
                "Lovelace", "Ada", "1815", "F", "London", "British", "GB", "P-1", "P", "N-1", "HMPO", "2025", "2035"),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    [Fact]
    public void Encrypt_Login_RoundTripsAllFields()
    {
        var result = RoundTrip(LoginSource());

        Assert.Equal(CipherType.Login, result.Type);
        Assert.Equal("GitHub", result.Name);
        Assert.Equal("note", result.Notes);
        Assert.Equal("f1", result.FolderId);
        Assert.True(result.Favorite);
        Assert.True(result.Reprompt);
        Assert.Equal("octo", result.Login!.Username);
        Assert.Equal("secret", result.Login.Password);
        Assert.Equal("totp-seed", result.Login.Totp);
        Assert.Equal("https://github.com", Assert.Single(result.Login.Uris).Uri);
        Assert.Equal(2, result.Fields.Count);
        Assert.Equal("Plain", result.Fields[0].Name);
        Assert.Equal(CipherFieldType.Hidden, result.Fields[1].Type);
    }

    [Fact]
    public void Encrypt_Fields_RoundTripsLinkedAndUnknownWireMetadata()
    {
        var source = LoginSource();
        source = new Cipher
        {
            Type = source.Type,
            Name = source.Name,
            Login = source.Login,
            Fields =
            [
                new CipherField("Linked password", null, CipherFieldType.Linked, LinkedId: 101),
                new CipherField("Future", "opaque", CipherFieldType.Unsupported, LinkedId: 999, RawType: 97),
            ],
        };

        var request = NewEncryptor().Encrypt(source, _userKey);

        Assert.Collection(
            request.Fields!,
            field =>
            {
                Assert.Equal(3, field.Type);
                Assert.Equal(101, field.LinkedId);
            },
            field =>
            {
                Assert.Equal(97, field.Type);
                Assert.Equal(999, field.LinkedId);
            });

        var result = RoundTrip(source);
        Assert.Equal(CipherFieldType.Linked, result.Fields[0].Type);
        Assert.Equal(101, result.Fields[0].LinkedId);
        Assert.Equal(CipherFieldType.Unsupported, result.Fields[1].Type);
        Assert.Equal(97, result.Fields[1].WireType);
    }

    [Fact]
    public void Encrypt_Login_RoundTripsFido2Credentials()
    {
        // A legacy unprefixed credential ID is normalized while every other field round-trips.
        var credential = Assert.Single(RoundTrip(LoginSource()).Login!.Fido2Credentials);

        Assert.Equal(CredentialId.Base64Prefix + "AQIDBA", credential.CredentialId);
        Assert.Equal("public-key", credential.KeyType);
        Assert.Equal("ECDSA", credential.KeyAlgorithm);
        Assert.Equal("P-256", credential.KeyCurve);
        Assert.Equal("priv-val", credential.KeyValue);
        Assert.Equal("github.com", credential.RpId);
        Assert.Equal("user-handle", credential.UserHandle);
        Assert.Equal("octo@x", credential.UserName);
        Assert.Equal(42, credential.Counter);
        Assert.Equal("GitHub", credential.RpName);
        Assert.Equal("Octo", credential.UserDisplayName);
        Assert.True(credential.Discoverable);
        Assert.Equal(DateTimeOffset.Parse("2026-06-24T00:00:00Z"), credential.CreationDate);
    }

    [Fact]
    public void Encrypt_Card_RoundTrips()
    {
        var source = new Cipher
        {
            Type = CipherType.Card,
            Name = "Visa",
            Card = new CipherCard("Jane", "4111", "12", "2030", "123", "Visa"),
        };

        var card = RoundTrip(source).Card!;
        Assert.Equal("Jane", card.CardholderName);
        Assert.Equal("4111", card.Number);
        Assert.Equal("Visa", card.Brand);
    }

    [Fact]
    public void Encrypt_Identity_RoundTrips()
    {
        var source = new Cipher
        {
            Type = CipherType.Identity,
            Name = "ID",
            Identity = new CipherIdentity("Mr", "Jane", null, "Doe", null, null, null, null, null,
                "jane@x", null, null, null, null, null, null, null, null),
        };

        var identity = RoundTrip(source).Identity!;
        Assert.Equal("Jane", identity.FirstName);
        Assert.Equal("Doe", identity.LastName);
        Assert.Equal("jane@x", identity.Email);
    }

    [Fact]
    public void Encrypt_SecureNote_RoundTrips()
    {
        var source = new Cipher
        {
            Type = CipherType.SecureNote,
            Name = "Note",
            Notes = "body",
            SecureNote = new CipherSecureNote(0),
        };

        var result = RoundTrip(source);
        Assert.Equal(0, result.SecureNote!.Type);
        Assert.Equal("body", result.Notes);
    }

    [Fact]
    public void Encrypt_Ssh_RoundTrips()
    {
        var source = new Cipher
        {
            Type = CipherType.SshKey,
            Name = "Key",
            Ssh = new CipherSsh("priv", "pub", "fp"),
        };

        var ssh = RoundTrip(source).Ssh!;
        Assert.Equal("priv", ssh.PrivateKey);
        Assert.Equal("pub", ssh.PublicKey);
        Assert.Equal("fp", ssh.Fingerprint);
    }

    [Fact]
    public void Encrypt_ArchivedCipher_PreservesArchivedDateInWriteRequest()
    {
        var archivedDate = new DateTimeOffset(2026, 8, 1, 12, 30, 0, TimeSpan.Zero);
        var source = new Cipher
        {
            Type = CipherType.Login,
            Name = "Archived",
            ArchivedDate = archivedDate,
            Login = new CipherLogin(null, null, null, []),
        };

        var request = NewEncryptor().Encrypt(source, _userKey);

        Assert.Equal(archivedDate, request.ArchivedDate);
    }

    [Theory]
    [InlineData(CipherType.BankAccount)]
    [InlineData(CipherType.DriversLicense)]
    [InlineData(CipherType.Passport)]
    [InlineData((CipherType)999)]
    public void Encrypt_TypeWithoutNegotiatedWriteSupport_Throws(CipherType type)
    {
        var source = new Cipher
        {
            Type = type,
            Name = "Unsupported",
        };

        var exception = Assert.Throws<UnsupportedCipherTypeWriteException>(
            () => NewEncryptor().Encrypt(source, _userKey));

        Assert.Equal(type, exception.CipherType);
        Assert.Contains(((int)type).ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains("JSON", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CipherType.BankAccount)]
    [InlineData(CipherType.DriversLicense)]
    [InlineData(CipherType.Passport)]
    public void Encrypt_NegotiatedNewCipherType_RoundTrips(CipherType type)
    {
        var capabilities = new VaultCapabilities(true, false, false);

        var result = RoundTrip(NewCipherTypeSource(type), capabilities);

        Assert.Equal(type, result.Type);
        Assert.NotNull(type switch
        {
            CipherType.BankAccount => result.BankAccount,
            CipherType.DriversLicense => result.DriversLicense,
            CipherType.Passport => result.Passport,
            _ => null,
        });
    }

    [Theory]
    [InlineData(CipherType.BankAccount)]
    [InlineData(CipherType.DriversLicense)]
    [InlineData(CipherType.Passport)]
    public void Encrypt_NegotiatedNewCipherTypeWithoutMatchingChild_Throws(CipherType type)
    {
        var exception = Assert.Throws<InvalidCipherWriteShapeException>(() =>
            NewEncryptor().Encrypt(
                new Cipher { Type = type, Name = "Missing typed child" },
                _userKey,
                new VaultCapabilities(true, false, false)));

        Assert.Equal(type, exception.CipherType);
    }

    [Fact]
    public void Encrypt_NegotiatedNewCipherTypeWithAdditionalChild_Throws()
    {
        var source = NewCipherTypeSource(CipherType.BankAccount);
        var malformed = new Cipher
        {
            Type = source.Type,
            Name = source.Name,
            BankAccount = source.BankAccount,
            Login = new CipherLogin(null, null, null, []),
        };

        Assert.Throws<InvalidCipherWriteShapeException>(() =>
            NewEncryptor().Encrypt(
                malformed,
                _userKey,
                new VaultCapabilities(true, false, false)));
    }

    [Fact]
    public void Encrypt_UnknownTypeIsRejectedEvenWhenNewCipherTypesAreEnabled()
    {
        Assert.Throws<UnsupportedCipherTypeWriteException>(() =>
            NewEncryptor().Encrypt(
                new Cipher { Type = (CipherType)999, Name = "Unknown" },
                _userKey,
                new VaultCapabilities(true, false, false)));
    }

    [Theory]
    [InlineData(CipherType.Login)]
    [InlineData(CipherType.SecureNote)]
    [InlineData(CipherType.Card)]
    [InlineData(CipherType.Identity)]
    [InlineData(CipherType.SshKey)]
    public void Encrypt_SupportedTypeWithoutMatchingChild_Throws(CipherType type)
    {
        var exception = Assert.Throws<InvalidCipherWriteShapeException>(() =>
            NewEncryptor().Encrypt(
                new Cipher { Type = type, Name = "Missing typed child" },
                _userKey));

        Assert.Equal(type, exception.CipherType);
    }

    [Fact]
    public void Encrypt_NonMatchingTypedChild_Throws()
    {
        var exception = Assert.Throws<InvalidCipherWriteShapeException>(() =>
            NewEncryptor().Encrypt(
                new Cipher
                {
                    Type = CipherType.Card,
                    Name = "Mismatched",
                    Login = new CipherLogin(null, null, null, []),
                },
                _userKey));

        Assert.Equal(CipherType.Card, exception.CipherType);
    }

    [Fact]
    public void Encrypt_AdditionalNonMatchingTypedChild_Throws()
    {
        var exception = Assert.Throws<InvalidCipherWriteShapeException>(() =>
            NewEncryptor().Encrypt(
                new Cipher
                {
                    Type = CipherType.Login,
                    Name = "Ambiguous",
                    Login = new CipherLogin(null, null, null, []),
                    Card = new CipherCard(null, null, null, null, null, null),
                },
                _userKey));

        Assert.Equal(CipherType.Login, exception.CipherType);
    }

    [Fact]
    public void Encrypt_WrapsPerCipherKey_NotEncryptedUnderUserKey()
    {
        var request = NewEncryptor().Encrypt(LoginSource(), _userKey);

        Assert.False(string.IsNullOrEmpty(request.Key));
        var cipherKey = _crypto.DecryptItemKey(request.Key!, _userKey);
        Assert.Equal(64, cipherKey.FullKey.Length);
        // Name decrypts with the per-cipher key, and fails MAC under the user key.
        Assert.Equal("GitHub", _crypto.DecryptToString(request.Name, cipherKey));
        Assert.Throws<CryptographicException>(() => _crypto.DecryptToString(request.Name, _userKey));
    }

    [Fact]
    public void EncryptFolderName_EncryptsDirectlyUnderUserKey()
    {
        var name = NewEncryptor().EncryptFolderName("Work", _userKey);

        Assert.StartsWith("2.", name);
        Assert.Equal("Work", _crypto.DecryptToString(name, _userKey));
    }

    // ---- R1: 编辑既有条目必须复用其条目密钥,不得轮换(否则损坏既有附件) ----

    // 造一个"服务端已下发"的条目密钥及其 UserKey 包裹串。
    private (SymmetricCryptoKey ItemKey, string Wrapped) IssueItemKey(int seed)
    {
        var itemKey = new SymmetricCryptoKey(Enumerable.Range(seed, 64).Select(i => (byte)i).ToArray());
        var wrapped = _crypto.Encrypt(itemKey.FullKey, _userKey).ToString();
        return (itemKey, wrapped);
    }

    [Fact]
    public void Encrypt_ExistingCipherWithKey_ReusesItemKeyAndKeepsKeyUnchanged()
    {
        var (itemKey, wrapped) = IssueItemKey(100);
        using (itemKey)
        {
            var source = new Cipher
            {
                Id = "c1",
                Key = wrapped,
                Type = CipherType.Login,
                Name = "GitHub",
                Login = new CipherLogin("octo", "secret", null, Array.Empty<CipherLoginUri>()),
            };

            var request = NewEncryptor().Encrypt(source, _userKey);

            // 回发的 Key 与服务端下发的一致(未轮换)。
            Assert.Equal(wrapped, request.Key);
            // 字段用复用的同一条目密钥加密(可解),而非新随机密钥。
            Assert.Equal("GitHub", _crypto.DecryptToString(request.Name, itemKey));
            Assert.Equal("secret", _crypto.DecryptToString(request.Login!.Password, itemKey));
        }
    }

    [Fact]
    public void Encrypt_NewCipherWithoutKey_GeneratesFreshItemKeyEachTime()
    {
        var source = new Cipher
        {
            Type = CipherType.Login,
            Name = "N",
            Login = new CipherLogin("u", "p", null, Array.Empty<CipherLoginUri>()),
        };

        var r1 = NewEncryptor().Encrypt(source, _userKey);
        var r2 = NewEncryptor().Encrypt(source, _userKey);

        Assert.False(string.IsNullOrEmpty(r1.Key));
        Assert.NotEqual(r1.Key, r2.Key); // 随机 IV + 随机密钥,每次不同
        using var k = _crypto.DecryptItemKey(r1.Key!, _userKey);
        Assert.Equal("N", _crypto.DecryptToString(r1.Name, k));
    }

    [Fact]
    public void Encrypt_ExistingLegacyCipherWithoutKey_KeepsUserKeyEncryptionAndNullKey()
    {
        // 旧格式条目:有 Id 但服务端从未下发条目密钥,字段直接在 UserKey 下。编辑不得引入新条目密钥。
        var source = new Cipher
        {
            Id = "legacy",
            Type = CipherType.Login,
            Name = "L",
            Login = new CipherLogin("u", "p", null, Array.Empty<CipherLoginUri>()),
        };

        var request = NewEncryptor().Encrypt(source, _userKey);

        Assert.Null(request.Key);
        Assert.Equal("L", _crypto.DecryptToString(request.Name, _userKey));
        Assert.Equal("p", _crypto.DecryptToString(request.Login!.Password, _userKey));
    }

    [Fact]
    public void Encrypt_EditingCipherWithAttachment_KeepsAttachmentDecryptable()
    {
        // R1 回归:编辑带附件的条目后,附件(其 akey 包裹在原条目密钥下)仍必须可解。
        var attCrypto = new AttachmentCryptoService(_crypto);
        var (itemKey, wrappedItemKey) = IssueItemKey(7);
        using (itemKey)
        using (var attKey = attCrypto.GenerateAttachmentKey())
        {
            var wrappedAttKey = attCrypto.WrapKey(attKey, itemKey); // 附件密钥包裹在原条目密钥下
            var encName = attCrypto.EncryptFileName("secret.txt", attKey);

            // 用户对该条目做了一次编辑(改名/改密码),领域 Cipher 携带原 Key。
            var edited = new Cipher
            {
                Id = "c1",
                Key = wrappedItemKey,
                Type = CipherType.Login,
                Name = "renamed",
                Login = new CipherLogin("u", "p2", null, Array.Empty<CipherLoginUri>()),
            };

            var request = NewEncryptor().Encrypt(edited, _userKey);

            // 服务端会执行 cipher.key = request.Key。据此解析条目密钥,附件 akey 必须仍能解开。
            using var resolvedItemKey = _crypto.DecryptItemKey(request.Key!, _userKey);
            using var resolvedAttKey = attCrypto.UnwrapKey(wrappedAttKey, resolvedItemKey);
            Assert.Equal("secret.txt", attCrypto.DecryptFileName(encName, resolvedAttKey));
        }
    }

    [Fact]
    public void EncryptForOrganizationTransfer_RewrapsExistingItemKeyAndPreservesAttachmentKey()
    {
        using var organizationKey = new SymmetricCryptoKey(
            Enumerable.Range(160, 64).Select(i => (byte)i).ToArray());
        var attachmentCrypto = new AttachmentCryptoService(_crypto);
        var (itemKey, wrappedItemKey) = IssueItemKey(32);
        using (itemKey)
        using (var attachmentKey = attachmentCrypto.GenerateAttachmentKey())
        {
            var wrappedAttachmentKey = attachmentCrypto.WrapKey(attachmentKey, itemKey);
            var encryptedAttachmentName = attachmentCrypto.EncryptFileName("transfer.txt", attachmentKey);
            var revision = new DateTimeOffset(2026, 8, 29, 19, 30, 0, TimeSpan.FromHours(8));
            var source = new Cipher
            {
                Id = "c-transfer",
                Key = wrappedItemKey,
                Type = CipherType.Login,
                Name = "Organization login",
                Notes = "private note",
                RevisionDate = revision,
                Login = new CipherLogin("octo", "secret", null, Array.Empty<CipherLoginUri>()),
                Attachments = [new CipherAttachment("att-1", "transfer.txt", 42, "42 Bytes")],
            };

            var request = NewEncryptor().EncryptForOrganizationTransfer(
                source,
                _userKey,
                organizationKey,
                "org-1");

            Assert.Equal("org-1", request.OrganizationId);
            Assert.NotNull(request.Key);
            Assert.NotEqual(wrappedItemKey, request.Key);
            Assert.Equal(revision.ToUniversalTime(), DateTimeOffset.Parse(request.LastKnownRevisionDate!));

            using var transferredItemKey = _crypto.DecryptItemKey(request.Key!, organizationKey);
            Assert.Equal(itemKey.FullKey, transferredItemKey.FullKey);
            Assert.Equal("Organization login", _crypto.DecryptToString(request.Name, transferredItemKey));
            Assert.Equal("secret", _crypto.DecryptToString(request.Login!.Password, transferredItemKey));

            using var transferredAttachmentKey = attachmentCrypto.UnwrapKey(
                wrappedAttachmentKey,
                transferredItemKey);
            Assert.Equal(
                "transfer.txt",
                attachmentCrypto.DecryptFileName(encryptedAttachmentName, transferredAttachmentKey));
        }
    }

    [Fact]
    public void EncryptForOrganizationTransfer_LegacyCipherWithoutAttachmentsUsesOrganizationKey()
    {
        using var organizationKey = new SymmetricCryptoKey(
            Enumerable.Range(64, 64).Select(i => (byte)i).ToArray());
        var source = new Cipher
        {
            Id = "legacy",
            Type = CipherType.Login,
            Name = "Legacy login",
            Login = new CipherLogin("user", "password", null, Array.Empty<CipherLoginUri>()),
        };

        var request = NewEncryptor().EncryptForOrganizationTransfer(
            source,
            _userKey,
            organizationKey,
            "org-2");

        Assert.Equal("org-2", request.OrganizationId);
        Assert.Null(request.Key);
        Assert.Equal("Legacy login", _crypto.DecryptToString(request.Name, organizationKey));
        Assert.Equal("password", _crypto.DecryptToString(request.Login!.Password, organizationKey));
        Assert.Throws<CryptographicException>(() => _crypto.DecryptToString(request.Name, _userKey));
    }

    [Fact]
    public void EncryptForOrganizationTransfer_LegacyCipherWithAttachmentsThrowsBeforeEncryption()
    {
        using var organizationKey = new SymmetricCryptoKey(
            Enumerable.Range(96, 64).Select(i => (byte)i).ToArray());
        var source = new Cipher
        {
            Id = "legacy-with-attachment",
            Type = CipherType.Login,
            Name = "Legacy login",
            Login = new CipherLogin("user", "password", null, Array.Empty<CipherLoginUri>()),
            Attachments = [new CipherAttachment("att-1", "legacy.txt", 10, "10 Bytes")],
        };

        var exception = Assert.Throws<LegacyCipherAttachmentTransferException>(() =>
            NewEncryptor().EncryptForOrganizationTransfer(
                source,
                _userKey,
                organizationKey,
                "org-3"));

        Assert.Equal(source.Id, exception.CipherId);
        Assert.Contains("undecryptable", exception.Message, StringComparison.Ordinal);
    }
}
