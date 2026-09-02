using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class VaultDecryptorTests
{
    private readonly CryptoService _crypto = new();
    private readonly SymmetricCryptoKey _userKey = new(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

    private string Enc(string plaintext, SymmetricCryptoKey key)
        => _crypto.Encrypt(Encoding.UTF8.GetBytes(plaintext), key).ToString();

    private static SyncResponse Sync(CipherDto[]? ciphers = null, FolderDto[]? folders = null, ProfileDto? profile = null)
        => new("sync", profile ?? new ProfileDto("u1", "me@example.com", "Me", null, null), folders ?? [], ciphers ?? []);

    private VaultDecryptor NewDecryptor() => new(_crypto);

    private CipherDto LoginDto() => new(
        Id: "c-login", Type: (int)CipherType.Login, Name: Enc("GitHub", _userKey), Notes: Enc("note", _userKey),
        Key: null, OrganizationId: null, FolderId: "f1", Favorite: true, Reprompt: 1,
        Login: new LoginDto(Enc("octo", _userKey), Enc("secret", _userKey), Enc("totp-seed", _userKey),
            [new LoginUriDto(Enc("https://github.com", _userKey), 0)]),
        Card: null, Identity: null, SecureNote: null, SshKey: null,
        Fields: null, CreationDate: DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
        RevisionDate: DateTimeOffset.Parse("2026-06-02T00:00:00Z"), DeletedDate: null);

    [Fact]
    public void Decrypt_Login_DecryptsAllFieldsAndFlags()
    {
        var result = NewDecryptor().Decrypt(Sync([LoginDto()]), _userKey, "https://vault.example");

        var cipher = Assert.Single(result.Ciphers);
        Assert.Equal(CipherType.Login, cipher.Type);
        Assert.Equal("GitHub", cipher.Name);
        Assert.Equal("note", cipher.Notes);
        Assert.True(cipher.Favorite);
        Assert.True(cipher.Reprompt);
        Assert.Equal("f1", cipher.FolderId);
        Assert.False(cipher.IsDeleted);
        Assert.NotNull(cipher.Login);
        Assert.Equal("octo", cipher.Login!.Username);
        Assert.Equal("secret", cipher.Login.Password);
        Assert.Equal("totp-seed", cipher.Login.Totp);
        Assert.Equal("https://github.com", Assert.Single(cipher.Login.Uris).Uri);
    }

    [Fact]
    public void Decrypt_Login_DecryptsFido2Credentials()
    {
        var dto = LoginDto() with
        {
            Login = new LoginDto(null, null, null, null,
            [
                new Fido2CredentialDto(
                    Enc("credential-id", _userKey),
                    Enc("public-key", _userKey),
                    Enc("ECDSA", _userKey),
                    Enc("P-256", _userKey),
                    Enc("private-key", _userKey),
                    Enc("github.com", _userKey),
                    Enc("user-handle", _userKey),
                    Enc("octo@example.com", _userKey),
                    Enc("42", _userKey),
                    Enc("GitHub", _userKey),
                    Enc("Octo Cat", _userKey),
                    Enc("true", _userKey),
                    DateTimeOffset.Parse("2026-06-24T00:00:00Z"))
            ]),
        };

        var result = NewDecryptor().Decrypt(Sync([dto]), _userKey, "https://vault.example");

        var login = Assert.Single(result.Ciphers).Login!;
        Assert.True(login.HasFido2Credentials);
        var credential = Assert.Single(login.Fido2Credentials);
        Assert.Equal("credential-id", credential.CredentialId);
        Assert.Equal("public-key", credential.KeyType);
        Assert.Equal("ECDSA", credential.KeyAlgorithm);
        Assert.Equal("P-256", credential.KeyCurve);
        Assert.Equal("private-key", credential.KeyValue);
        Assert.Equal("github.com", credential.RpId);
        Assert.Equal("user-handle", credential.UserHandle);
        Assert.Equal("octo@example.com", credential.UserName);
        Assert.Equal(42, credential.Counter);
        Assert.Equal("GitHub", credential.RpName);
        Assert.Equal("Octo Cat", credential.UserDisplayName);
        Assert.True(credential.Discoverable);
        Assert.Equal(DateTimeOffset.Parse("2026-06-24T00:00:00Z"), credential.CreationDate);
    }

    [Fact]
    public void Decrypt_Card_Identity_SecureNote_Ssh_MapToTypedChildren()
    {
        var card = new CipherDto("c-card", (int)CipherType.Card, Enc("Visa", _userKey), null, null, null, null, false, 0,
            null, new CardDto(Enc("Jane", _userKey), Enc("4111", _userKey), Enc("12", _userKey), Enc("2030", _userKey), Enc("123", _userKey), Enc("Visa", _userKey)),
            null, null, null, null, null, null, null);
        var identity = new CipherDto("c-id", (int)CipherType.Identity, Enc("ID", _userKey), null, null, null, null, false, 0,
            null, null, new IdentityDto(Enc("Mr", _userKey), Enc("Jane", _userKey), null, Enc("Doe", _userKey), null, null, null, null, null,
                Enc("jane@example.com", _userKey), null, null, null, null, null, null, null, null),
            null, null, null, null, null, null);
        var note = new CipherDto("c-note", (int)CipherType.SecureNote, Enc("Note", _userKey), Enc("body", _userKey), null, null, null, false, 0,
            null, null, null, new SecureNoteDto(0), null, null, null, null, null);
        var ssh = new CipherDto("c-ssh", (int)CipherType.SshKey, Enc("Key", _userKey), null, null, null, null, false, 0,
            null, null, null, null, new SshKeyDto(Enc("priv", _userKey), Enc("pub", _userKey), Enc("fp", _userKey)),
            null, null, null, null);

        var result = NewDecryptor().Decrypt(Sync([card, identity, note, ssh]), _userKey, "https://vault.example");

        var c = result.Ciphers.Single(x => x.Type == CipherType.Card);
        Assert.Equal("Jane", c.Card!.CardholderName);
        Assert.Equal("4111", c.Card.Number);
        Assert.Equal("Visa", c.Card.Brand);

        var id = result.Ciphers.Single(x => x.Type == CipherType.Identity);
        Assert.Equal("Jane", id.Identity!.FirstName);
        Assert.Equal("Doe", id.Identity.LastName);
        Assert.Equal("jane@example.com", id.Identity.Email);

        var n = result.Ciphers.Single(x => x.Type == CipherType.SecureNote);
        Assert.Equal(0, n.SecureNote!.Type);
        Assert.Equal("body", n.Notes);

        var s = result.Ciphers.Single(x => x.Type == CipherType.SshKey);
        Assert.Equal("priv", s.Ssh!.PrivateKey);
        Assert.Equal("pub", s.Ssh.PublicKey);
        Assert.Equal("fp", s.Ssh.Fingerprint);
    }

    [Fact]
    public void Decrypt_NewCipherTypes_MapsEveryTypedChild()
    {
        var bank = new CipherDto(
            "bank", 6, Enc("Bank", _userKey), null, null, null, null, false, 0,
            null, null, null, null, null, null, null, null, null,
            BankAccount: new BankAccountDto(
                Enc("Example Bank", _userKey), Enc("Ada", _userKey), Enc("Checking", _userKey),
                Enc("123456", _userKey), Enc("987654", _userKey), Enc("001", _userKey),
                Enc("1234", _userKey), Enc("EXAMPLEX", _userKey), Enc("GB00TEST", _userKey),
                Enc("555-0100", _userKey)));
        var license = new CipherDto(
            "license", 7, Enc("License", _userKey), null, null, null, null, false, 0,
            null, null, null, null, null, null, null, null, null,
            DriversLicense: new DriversLicenseDto(
                Enc("Ada", _userKey), Enc("M", _userKey), Enc("Lovelace", _userKey),
                Enc("1815-12-10", _userKey), Enc("DL-123", _userKey), Enc("GB", _userKey),
                Enc("London", _userKey), Enc("2025-01-01", _userKey), Enc("2030-01-01", _userKey),
                Enc("DVLA", _userKey), Enc("B", _userKey)));
        var passport = new CipherDto(
            "passport", 8, Enc("Passport", _userKey), null, null, null, null, false, 0,
            null, null, null, null, null, null, null, null, null,
            Passport: new PassportDto(
                Enc("Lovelace", _userKey), Enc("Ada", _userKey), Enc("1815-12-10", _userKey),
                Enc("F", _userKey), Enc("London", _userKey), Enc("British", _userKey),
                Enc("GB", _userKey), Enc("P123", _userKey), Enc("P", _userKey),
                Enc("N123", _userKey), Enc("HMPO", _userKey), Enc("2025-01-01", _userKey),
                Enc("2035-01-01", _userKey)));

        var result = NewDecryptor().Decrypt(Sync([bank, license, passport]), _userKey, "https://vault.example");

        Assert.Equal("123456", result.Ciphers.Single(cipher => cipher.Type == CipherType.BankAccount).BankAccount!.AccountNumber);
        Assert.Equal("DL-123", result.Ciphers.Single(cipher => cipher.Type == CipherType.DriversLicense).DriversLicense!.LicenseNumber);
        Assert.Equal("P123", result.Ciphers.Single(cipher => cipher.Type == CipherType.Passport).Passport!.PassportNumber);
    }

    [Fact]
    public void Decrypt_Folders_DecryptNames()
    {
        var folders = new[]
        {
            new FolderDto("f1", Enc("Work", _userKey), DateTimeOffset.Parse("2026-06-01T00:00:00Z")),
            new FolderDto("f2", Enc("Personal", _userKey), DateTimeOffset.Parse("2026-06-01T00:00:00Z")),
        };

        var result = NewDecryptor().Decrypt(Sync(folders: folders), _userKey, "https://vault.example");

        Assert.Equal(2, result.Folders.Count);
        Assert.Equal("Work", result.Folders[0].Name);
        Assert.Equal("Personal", result.Folders[1].Name);
    }

    [Fact]
    public void Decrypt_BadFolder_IsSkipped_GoodFolderAndCiphersRemain()
    {
        using var wrongKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var folders = new[]
        {
            new FolderDto("f-bad", Enc("cannot decrypt", wrongKey), DateTimeOffset.Parse("2026-06-01T00:00:00Z")),
            new FolderDto("f-good", Enc("Work", _userKey), DateTimeOffset.Parse("2026-06-01T00:00:00Z")),
        };

        var result = NewDecryptor().Decrypt(Sync(ciphers: [LoginDto()], folders: folders), _userKey, "https://vault.example");

        Assert.Single(result.Folders);
        Assert.Equal("Work", result.Folders[0].Name);
        Assert.Single(result.Ciphers);
        Assert.Equal("GitHub", result.Ciphers[0].Name);
        Assert.Equal(0, result.SkippedCipherCount);
        Assert.Equal(1, result.SkippedFolderCount);
    }

    [Fact]
    public void Decrypt_ItemLevelKey_OverridesUserKey()
    {
        var itemKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var cipherKeyEnc = _crypto.Encrypt(itemKey.FullKey, _userKey).ToString();
        // These fields use the item key; using the user key would fail MAC validation.
        var dto = new CipherDto("c-key", (int)CipherType.Login, Enc("Secret Item", itemKey), null, cipherKeyEnc, null, null, false, 0,
            new LoginDto(Enc("alice", itemKey), Enc("pw", itemKey), null, null),
            null, null, null, null, null, null, null, null);

        var result = NewDecryptor().Decrypt(Sync([dto]), _userKey, "https://vault.example");

        var cipher = Assert.Single(result.Ciphers);
        Assert.Equal("Secret Item", cipher.Name);
        Assert.Equal("alice", cipher.Login!.Username);
        Assert.Equal(0, result.SkippedCipherCount);
    }

    [Fact]
    public void Decrypt_CustomFields_PreserveTypeAndValue()
    {
        var fields = new[]
        {
            new FieldDto((int)CipherFieldType.Text, Enc("Plain", _userKey), Enc("v1", _userKey)),
            new FieldDto((int)CipherFieldType.Hidden, Enc("Secret", _userKey), Enc("v2", _userKey)),
            new FieldDto((int)CipherFieldType.Boolean, Enc("Flag", _userKey), Enc("true", _userKey)),
        };
        var dto = new CipherDto("c-fields", (int)CipherType.Login, Enc("WithFields", _userKey), null, null, null, null, false, 0,
            new LoginDto(null, null, null, null), null, null, null, null, fields, null, null, null);

        var result = NewDecryptor().Decrypt(Sync([dto]), _userKey, "https://vault.example");

        var cipher = Assert.Single(result.Ciphers);
        Assert.Equal(3, cipher.Fields.Count);
        Assert.Equal(CipherFieldType.Text, cipher.Fields[0].Type);
        Assert.Equal("Plain", cipher.Fields[0].Name);
        Assert.Equal("v1", cipher.Fields[0].Value);
        Assert.Equal(CipherFieldType.Hidden, cipher.Fields[1].Type);
        Assert.Equal("v2", cipher.Fields[1].Value);
        Assert.Equal(CipherFieldType.Boolean, cipher.Fields[2].Type);
    }

    [Fact]
    public void Decrypt_CustomFields_PreservesLinkedAndUnknownWireMetadata()
    {
        var fields = new[]
        {
            new FieldDto(3, Enc("Linked password", _userKey), null, 101),
            new FieldDto(97, Enc("Future field", _userKey), Enc("opaque", _userKey), 999),
        };
        var dto = LoginDto() with { Fields = fields };

        var cipher = Assert.Single(
            NewDecryptor().Decrypt(Sync([dto]), _userKey, "https://vault.example").Ciphers);

        Assert.Collection(
            cipher.Fields,
            field =>
            {
                Assert.Equal(CipherFieldType.Linked, field.Type);
                Assert.Equal(3, field.WireType);
                Assert.Equal(101, field.LinkedId);
            },
            field =>
            {
                Assert.Equal(CipherFieldType.Unsupported, field.Type);
                Assert.Equal(97, field.RawType);
                Assert.Equal(97, field.WireType);
                Assert.Equal(999, field.LinkedId);
                Assert.Equal("opaque", field.Value);
            });
    }

    [Fact]
    public void Decrypt_BadCipher_IsSkippedGoodRemains()
    {
        using var wrongKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var bad = new CipherDto("c-bad", (int)CipherType.Login, Enc("Bad", wrongKey), null, null, null, null, false, 0,
            new LoginDto(Enc("x", wrongKey), null, null, null), null, null, null, null, null, null, null, null);

        var result = NewDecryptor().Decrypt(Sync([bad, LoginDto()]), _userKey, "https://vault.example");

        var cipher = Assert.Single(result.Ciphers);
        Assert.Equal("GitHub", cipher.Name);
        Assert.Equal(1, result.SkippedCipherCount);
    }

    [Fact]
    public void Decrypt_OrganizationCipher_IsClassifiedSeparatelyFromCorruptCipher()
    {
        var organizationCipher = LoginDto() with { OrganizationId = "org-1" };

        var result = NewDecryptor().Decrypt(
            Sync([organizationCipher, LoginDto()]),
            _userKey,
            "https://vault.example");

        Assert.Single(result.Ciphers);
        Assert.Equal(0, result.SkippedCipherCount);
        Assert.Equal(1, result.UnsupportedOrganizationCipherCount);
    }

    [Fact]
    public void Decrypt_OrganizationVault_UsesMembershipKeyAndProjectsCollectionsPoliciesAndPermissions()
    {
        using var rsa = RSA.Create(2048);
        using var organizationKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        using var itemKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var privateKeyBytes = rsa.ExportPkcs8PrivateKey();
        var encryptedPrivateKey = _crypto.Encrypt(privateKeyBytes, _userKey).ToString();
        CryptographicOperations.ZeroMemory(privateKeyBytes);
        var encryptedOrganizationKey = _crypto.EncryptRsa(
            organizationKey.FullKey,
            rsa.ExportSubjectPublicKeyInfo()).ToString();
        var encryptedItemKey = _crypto.Encrypt(itemKey.FullKey, organizationKey).ToString();

        var profile = new ProfileDto(
            "u1",
            "me@example.com",
            "Me",
            null,
            encryptedPrivateKey,
            [new ProfileOrganizationDto("org-1", "Team", encryptedOrganizationKey, 2, 2)]);
        var cipher = new CipherDto(
            "c-org",
            (int)CipherType.Login,
            Enc("Shared login", itemKey),
            null,
            encryptedItemKey,
            "org-1",
            null,
            false,
            0,
            new LoginDto(Enc("shared-user", itemKey), Enc("shared-password", itemKey), null, null),
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            CollectionIds: ["collection-1"],
            Edit: false,
            ViewPassword: false,
            Permissions: new CipherPermissionsDto(Delete: false, Restore: false));
        var collection = new CollectionDto(
            "collection-1",
            "org-1",
            Enc("Engineering", organizationKey),
            ReadOnly: true,
            HidePasswords: true,
            Manage: false);
        using var policyDocument = JsonDocument.Parse("{\"disableHideEmail\":true}");
        var policy = new OrganizationPolicyDto(
            "policy-1",
            "org-1",
            7,
            true,
            policyDocument.RootElement.Clone());
        var sync = new SyncResponse(
            "sync",
            profile,
            [],
            [cipher],
            [collection],
            [policy]);

        var result = NewDecryptor().Decrypt(sync, _userKey, "https://vault.example");

        var decrypted = Assert.Single(result.Ciphers);
        Assert.Equal("Shared login", decrypted.Name);
        Assert.Equal("shared-user", decrypted.Login!.Username);
        Assert.Equal("shared-password", decrypted.Login.Password);
        Assert.Equal("org-1", decrypted.OrganizationId);
        Assert.Equal(["collection-1"], decrypted.CollectionIds);
        Assert.False(decrypted.CanEdit);
        Assert.False(decrypted.CanViewPassword);
        Assert.False(decrypted.CanDelete);
        Assert.False(decrypted.CanRestore);
        Assert.Equal(0, result.UnsupportedOrganizationCipherCount);

        var decryptedOrganization = Assert.Single(result.Organizations);
        Assert.Equal("Team", decryptedOrganization.Name);
        Assert.Equal(encryptedOrganizationKey, decryptedOrganization.EncryptedKey);
        var decryptedCollection = Assert.Single(result.Collections);
        Assert.Equal("Engineering", decryptedCollection.Name);
        Assert.True(decryptedCollection.ReadOnly);
        Assert.True(decryptedCollection.HidePasswords);
        var decryptedPolicy = Assert.Single(result.Policies);
        Assert.Equal(7, decryptedPolicy.Type);
        Assert.Contains("disableHideEmail", decryptedPolicy.DataJson);
    }

    [Fact]
    public void Decrypt_RestrictedItemPolicy_RemovesRestrictedPersonalCipherFromSnapshot()
    {
        using var document = JsonDocument.Parse("[3]");
        var profile = new ProfileDto(
            "u1",
            "me@example.com",
            "Me",
            null,
            null,
            [new ProfileOrganizationDto("org-1", "Team", null, 2, 2)]);
        var card = new CipherDto(
            "card-1",
            (int)CipherType.Card,
            Enc("Restricted card", _userKey),
            null,
            null,
            null,
            null,
            false,
            0,
            null,
            new CardDto(null, null, null, null, null, null),
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        var policy = new OrganizationPolicyDto(
            "policy-1",
            "org-1",
            (int)OrganizationPolicyType.RestrictedItemTypes,
            true,
            document.RootElement.Clone());

        var result = NewDecryptor().Decrypt(
            new SyncResponse("sync", profile, [], [card], [], [policy]),
            _userKey,
            "https://vault.example");

        Assert.Empty(result.Ciphers);
        Assert.Equal(0, result.SkippedCipherCount);
    }

    [Fact]
    public void Decrypt_PrefersNonEmptyPoliciesNewAndOrganizationsNewForPolicyContext()
    {
        var profile = new ProfileDto(
            "u1",
            "me@example.com",
            "Me",
            null,
            null,
            [new ProfileOrganizationDto("org-confirmed", "Confirmed", null, 2, 2)])
        {
            OrganizationsNew =
            [
                new ProfileOrganizationDto(
                    "org-accepted",
                    "Accepted",
                    null,
                    1,
                    0,
                    UsePolicies: true),
            ],
        };
        var legacy = new OrganizationPolicyDto(
            "legacy-policy",
            "org-confirmed",
            (int)OrganizationPolicyType.DisableSend,
            true,
            default);
        var current = new OrganizationPolicyDto(
            "current-policy",
            "org-accepted",
            (int)OrganizationPolicyType.RemoveUnlockWithPin,
            true,
            default);

        var result = NewDecryptor().Decrypt(
            new SyncResponse(
                "sync",
                profile,
                [],
                [],
                [],
                [legacy],
                PoliciesNew: [current]),
            _userKey,
            "https://vault.example");

        Assert.Equal("current-policy", Assert.Single(result.Policies).Id);
        Assert.Equal("org-confirmed", Assert.Single(result.Organizations).Id);
        var context = Assert.Single(result.PolicyOrganizations!);
        Assert.Equal("org-accepted", context.Id);
        Assert.Equal(1, context.Status);
        Assert.Equal(0, context.Role);
        Assert.True(VaultPolicyEvaluator.IsEnabledForUser(
            OrganizationPolicyType.RemoveUnlockWithPin,
            result.PolicyOrganizations!,
            result.Policies));
    }

    [Fact]
    public void Decrypt_EmptyPoliciesNewFallsBackButEmptyOrganizationsNewDoesNot()
    {
        var profile = new ProfileDto(
            "u1",
            "me@example.com",
            "Me",
            null,
            null,
            [new ProfileOrganizationDto("org-confirmed", "Confirmed", null, 2, 2)])
        {
            OrganizationsNew = [],
        };
        var legacy = new OrganizationPolicyDto(
            "legacy-policy",
            "org-confirmed",
            (int)OrganizationPolicyType.DisableSend,
            true,
            default);

        var result = NewDecryptor().Decrypt(
            new SyncResponse(
                "sync",
                profile,
                [],
                [],
                [],
                [legacy],
                PoliciesNew: []),
            _userKey,
            "https://vault.example");

        Assert.Equal("legacy-policy", Assert.Single(result.Policies).Id);
        Assert.Empty(result.PolicyOrganizations!);
    }

    [Fact]
    public void Decrypt_MapsDomainsAndSyncSendIntoSnapshot()
    {
        var sendCrypto = new SendCryptoService(_crypto);
        var seed = sendCrypto.GenerateSeed();
        using var sendKey = sendCrypto.DeriveCryptoKey(seed);
        var send = new SendResponseDto(
            "send-1",
            "access-1",
            (int)SendType.Text,
            sendCrypto.EncryptField("Offline Send", sendKey),
            null,
            new SendTextDto(sendCrypto.EncryptField("secret", sendKey), false),
            null,
            sendCrypto.WrapSeed(seed, _userKey),
            null,
            0,
            null,
            (int)SendAuthType.None,
            false,
            false,
            DateTimeOffset.UnixEpoch,
            null,
            DateTimeOffset.UnixEpoch.AddDays(1),
            "send");
        var domains = new DomainsDto(
            [["example.com", "example.net"]],
            [new GlobalEquivalentDomainDto(3, ["google.com", "gmail.com"], false)],
            "domains");

        var result = NewDecryptor().Decrypt(
            new SyncResponse(
                "sync",
                new ProfileDto("u1", "me@example.com", "Me", null, null),
                [],
                [],
                Sends: [send],
                Domains: domains),
            _userKey,
            "https://vault.example");

        var decrypted = Assert.Single(result.Sends);
        Assert.Equal("Offline Send", decrypted.Name);
        Assert.Equal("secret", decrypted.Text!.Content);
        Assert.Equal(0, result.SkippedSendCount);
        Assert.Equal("example.net", result.Domains!.EquivalentDomains[0][1]);
        Assert.Equal("gmail.com", Assert.Single(result.Domains.GlobalEquivalentDomains).Domains[1]);
    }

    [Fact]
    public void Decrypt_DeletedDate_MarksTrash()
    {
        var dto = LoginDto() with { DeletedDate = DateTimeOffset.Parse("2026-06-10T00:00:00Z") };

        var result = NewDecryptor().Decrypt(Sync([dto]), _userKey, "https://vault.example");

        Assert.True(Assert.Single(result.Ciphers).IsDeleted);
    }

    [Fact]
    public void Decrypt_Account_FromProfileAndServer()
    {
        var result = NewDecryptor().Decrypt(Sync(profile: new ProfileDto("u1", "me@example.com", "Me", null, null)),
            _userKey, "https://vault.example");

        Assert.Equal("me@example.com", result.Account.Email);
        Assert.Equal("https://vault.example", result.Account.ServerUrl);
        Assert.Equal("M", result.Account.Initial);
    }

    [Fact]
    public void Decrypt_MapsAccountClaimAndOrganizationProductTierMetadata()
    {
        var profile = new ProfileDto(
            "u1",
            "me@example.com",
            "Me",
            null,
            null,
            [new ProfileOrganizationDto(
                "org-1",
                "Team",
                null,
                2,
                0,
                ProductTierType: 3,
                UserIsClaimedByOrganization: true)]);

        var result = NewDecryptor().Decrypt(
            Sync(profile: profile),
            _userKey,
            "https://vault.example");

        Assert.True(result.Account.UserIsClaimedByOrganization);
        Assert.Equal(3, Assert.Single(result.Organizations).ProductTierType);
    }

    [Fact]
    public void Decrypt_MissingDeletionMetadataRemainsUnknown()
    {
        var profile = new ProfileDto(
            "u1",
            "me@example.com",
            "Me",
            null,
            null,
            [new ProfileOrganizationDto("org-1", "Team", null, 2, 0)]);

        var result = NewDecryptor().Decrypt(
            Sync(profile: profile),
            _userKey,
            "https://vault.example");

        Assert.Null(result.Account.UserIsClaimedByOrganization);
        Assert.Null(Assert.Single(result.Organizations).ProductTierType);
    }

    [Fact]
    public void Decrypt_OrganizationsNewExplicitClaimOverridesLegacyFalse()
    {
        var profile = new ProfileDto(
            "u1",
            "me@example.com",
            "Me",
            null,
            null,
            [new ProfileOrganizationDto(
                "org-legacy",
                "Legacy",
                null,
                2,
                0,
                UserIsClaimedByOrganization: false)])
        {
            OrganizationsNew =
            [
                new ProfileOrganizationDto(
                    "org-new",
                    "New",
                    null,
                    2,
                    0,
                    UserIsClaimedByOrganization: true),
            ],
        };

        var result = NewDecryptor().Decrypt(
            Sync(profile: profile),
            _userKey,
            "https://vault.example");

        Assert.True(result.Account.UserIsClaimedByOrganization);
    }

    [Fact]
    public void Decrypt_AllExplicitFalseClaimsMapFalse()
    {
        var profile = new ProfileDto(
            "u1",
            "me@example.com",
            "Me",
            null,
            null,
            [new ProfileOrganizationDto(
                "org-1",
                "Team",
                null,
                2,
                0,
                UserIsClaimedByOrganization: false)]);

        var result = NewDecryptor().Decrypt(
            Sync(profile: profile),
            _userKey,
            "https://vault.example");

        Assert.False(result.Account.UserIsClaimedByOrganization);
    }

    [Fact]
    public void Decrypt_MixedFalseAndMissingClaimsRemainUnknown()
    {
        var profile = new ProfileDto(
            "u1",
            "me@example.com",
            "Me",
            null,
            null,
            [
                new ProfileOrganizationDto(
                    "org-known",
                    "Known",
                    null,
                    2,
                    0,
                    UserIsClaimedByOrganization: false),
                new ProfileOrganizationDto("org-unknown", "Unknown", null, 2, 2),
            ]);

        var result = NewDecryptor().Decrypt(
            Sync(profile: profile),
            _userKey,
            "https://vault.example");

        Assert.Null(result.Account.UserIsClaimedByOrganization);
    }

    // C2: BuildAccount must use profile.Id, not hard-coded empty string.
    [Fact]
    public void Decrypt_UsesProfileIdAsAccountId()
    {
        var result = NewDecryptor().Decrypt(
            Sync(profile: new ProfileDto("real-user-id-xyz", "me@example.com", "Me", null, null)),
            _userKey, "https://vault.example");

        Assert.Equal("real-user-id-xyz", result.Account.Id);
    }

    // C2: null profile must throw InvalidDataException (profile is required).
    [Fact]
    public void Decrypt_NullProfile_ThrowsInvalidDataException()
    {
        var sync = new SyncResponse("sync", null, [], []);
        Assert.Throws<InvalidDataException>(() =>
            NewDecryptor().Decrypt(sync, _userKey, "https://vault.example"));
    }

    // C2: blank/whitespace profile Id must throw InvalidDataException.
    [Fact]
    public void Decrypt_BlankProfileId_ThrowsInvalidDataException()
    {
        var sync = new SyncResponse("sync",
            new ProfileDto("  ", "me@example.com", "Me", null, null), [], []);
        Assert.Throws<InvalidDataException>(() =>
            NewDecryptor().Decrypt(sync, _userKey, "https://vault.example"));
    }
}
