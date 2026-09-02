using App.Services;
using App.ViewModels.Models;
using Core.Enums;
using Core.Models;
using Core.Services;
using Xunit;

namespace App.Tests;

public class CipherDraftMapperTests
{
    private static CipherEditorDraft LoginDraft()
    {
        var draft = new CipherEditorDraft { Type = VaultItemKind.Login, Name = "  GitHub  ", FolderId = "f1", Favorite = true, Reprompt = true, Notes = "note" };
        draft.Login.Username = "octo";
        draft.Login.Password = "pw";
        draft.Login.Totp = "otp";
        draft.Login.Uris[0].Uri = "https://github.com";
        draft.Login.Uris[0].Match = 0;
        draft.CustomFields.Add(new CustomFieldEditorDraft { Name = "Text", Type = CipherEditorFieldType.Text, Value = "v" });
        draft.CustomFields.Add(new CustomFieldEditorDraft { Name = "Hidden", Type = CipherEditorFieldType.Hidden, Value = "s" });
        draft.CustomFields.Add(new CustomFieldEditorDraft { Name = "Bool", Type = CipherEditorFieldType.Boolean, BooleanValue = true });
        draft.CustomFields.Add(new CustomFieldEditorDraft { Name = "  ", Value = "blank-name-dropped" });
        return draft;
    }

    [Fact]
    public void ToCipher_New_TrimsNameMapsFieldsAndHasEmptyId()
    {
        var cipher = CipherDraftMapper.ToCipher(LoginDraft(), original: null);

        Assert.Equal("", cipher.Id);
        Assert.Null(cipher.Key); // 新建条目无原密钥,由 CipherEncryptor 生成随机条目密钥
        Assert.Equal(CipherType.Login, cipher.Type);
        Assert.Equal("GitHub", cipher.Name);
        Assert.Equal("f1", cipher.FolderId);
        Assert.True(cipher.Favorite);
        Assert.True(cipher.Reprompt);
        Assert.Equal("note", cipher.Notes);
        Assert.NotNull(cipher.Login);
        Assert.Equal("octo", cipher.Login!.Username);
        Assert.Equal("otp", cipher.Login.Totp);
        Assert.Equal("https://github.com", Assert.Single(cipher.Login.Uris).Uri);
        Assert.Empty(cipher.Login.Fido2Credentials);

        Assert.Collection(cipher.Fields,
            f => { Assert.Equal("Text", f.Name); Assert.Equal("v", f.Value); Assert.Equal(CipherFieldType.Text, f.Type); },
            f => { Assert.Equal("Hidden", f.Name); Assert.Equal("s", f.Value); Assert.Equal(CipherFieldType.Hidden, f.Type); },
            f => { Assert.Equal("Bool", f.Name); Assert.Equal("true", f.Value); Assert.Equal(CipherFieldType.Boolean, f.Type); });
    }

    [Fact]
    public void ToCipher_EmptyStringsBecomeNull()
    {
        var draft = new CipherEditorDraft { Type = VaultItemKind.Login, Name = "n" };
        var cipher = CipherDraftMapper.ToCipher(draft, null);

        Assert.Null(cipher.Notes);
        Assert.Null(cipher.Login!.Username);
        Assert.Null(cipher.Login.Password);
        Assert.Empty(cipher.Login.Uris); // blank default URI dropped
        Assert.Null(cipher.FolderId);
    }

    [Fact]
    public void ToCipher_Edit_PreservesId_Dates_OrgId_AndPasskeys()
    {
        var passkey = new CipherFido2Credential("cred", "kt", "alg", "P-256", "kv", "github.com", "uh", "user", 7, "GitHub", "Octo", true, DateTimeOffset.UnixEpoch);
        var original = new Cipher
        {
            Id = "edit-1",
            Type = CipherType.Login,
            OrganizationId = "org-1",
            CollectionIds = ["collection-1"],
            Key = "2.wrapped-item-key",
            Name = "Old",
            CreationDate = DateTimeOffset.UnixEpoch,
            RevisionDate = DateTimeOffset.UnixEpoch.AddDays(3),
            Login = new CipherLogin("old", null, null, new[] { new CipherLoginUri("https://old", null) }) { Fido2Credentials = new[] { passkey } },
        };
        var draft = CipherDraftMapper.ToDraft(original);
        draft.Name = "New";

        var updated = CipherDraftMapper.ToCipher(draft, original);

        Assert.Equal("edit-1", updated.Id);
        Assert.Equal("org-1", updated.OrganizationId);
        Assert.Equal("collection-1", draft.CollectionId);
        Assert.Equal(["collection-1"], updated.CollectionIds);
        // 编辑必须保留原条目的包裹密钥,供 CipherEncryptor 复用同一条目密钥(不轮换),否则损坏既有附件(R1)。
        Assert.Equal("2.wrapped-item-key", updated.Key);
        Assert.Equal("New", updated.Name);
        Assert.Equal(original.CreationDate, updated.CreationDate);
        Assert.Equal(original.RevisionDate, updated.RevisionDate);
        Assert.Equal("cred", Assert.Single(updated.Login!.Fido2Credentials).CredentialId);
    }

    [Fact]
    public void ToDraft_CapturesImmutableEditBaseline()
    {
        var collections = new[] { "collection-1", "collection-2" };
        var original = new Cipher
        {
            Id = "edit-1",
            Type = CipherType.Login,
            OrganizationId = "org-1",
            CollectionIds = collections,
            Key = "2.wrapped-item-key",
            Name = "Old",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("old", "secret", null, []),
        };

        var draft = CipherDraftMapper.ToDraft(original);
        collections[0] = "mutated";

        var baseline = Assert.IsType<CipherEditBaseline>(draft.EditBaseline);
        Assert.Equal("edit-1", baseline.CipherId);
        Assert.Equal(CipherType.Login, baseline.CipherType);
        Assert.Equal(original.RevisionDate, baseline.RevisionDate);
        Assert.Equal("org-1", baseline.OrganizationId);
        Assert.Equal("2.wrapped-item-key", baseline.Key);
        Assert.Equal(["collection-1", "collection-2"], baseline.CollectionIds);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)baseline.CollectionIds).Add("collection-3"));
    }

    [Fact]
    public void ToCipher_EditWithNewerSnapshotRevision_IsRejectedInsteadOfUsingNewRevision()
    {
        var opened = new Cipher
        {
            Id = "edit-1",
            Type = CipherType.Login,
            Name = "Opened",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("old", "secret", null, []),
        };
        var draft = CipherDraftMapper.ToDraft(opened);
        draft.Name = "Local edit";
        var refreshed = new Cipher
        {
            Id = opened.Id,
            Type = opened.Type,
            Name = "Remote edit",
            RevisionDate = opened.RevisionDate.AddMinutes(1),
            Login = opened.Login,
        };

        Assert.Throws<CipherEditConflictException>(() =>
            CipherDraftMapper.ToCipher(draft, refreshed));
        Assert.Equal(opened.RevisionDate, draft.EditBaseline!.RevisionDate);
    }

    [Fact]
    public void ToCipher_EditWithChangedDraftType_IsRejected()
    {
        var original = new Cipher
        {
            Id = "edit-1",
            Type = CipherType.Login,
            Name = "Opened",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("user", "secret", null, []),
        };
        var draft = CipherDraftMapper.ToDraft(original);
        draft.Type = VaultItemKind.Card;

        Assert.Throws<CipherEditConflictException>(() =>
            CipherDraftMapper.ToCipher(draft, original));
    }

    [Fact]
    public void ToCipher_EditUsesOpeningOwnershipKeyCollectionsAndRevision()
    {
        var original = new Cipher
        {
            Id = "edit-1",
            Type = CipherType.Login,
            OrganizationId = "org-1",
            CollectionIds = ["collection-1", "collection-2"],
            Key = "2.wrapped-item-key",
            Name = "Opened",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("old", "secret", null, []),
        };
        var draft = CipherDraftMapper.ToDraft(original);
        draft.OrganizationId = "other-org";
        draft.CollectionId = "other-collection";

        var updated = CipherDraftMapper.ToCipher(draft, original);

        Assert.Equal(original.OrganizationId, updated.OrganizationId);
        Assert.Equal(original.CollectionIds, updated.CollectionIds);
        Assert.Equal(original.Key, updated.Key);
        Assert.Equal(original.RevisionDate, updated.RevisionDate);
    }

    [Fact]
    public void ToCipher_NewOrganizationDraft_MapsDestination()
    {
        var draft = LoginDraft();
        draft.OrganizationId = "org-1";
        draft.CollectionId = "collection-1";

        var cipher = CipherDraftMapper.ToCipher(draft, original: null);

        Assert.Equal("org-1", cipher.OrganizationId);
        Assert.Equal(["collection-1"], cipher.CollectionIds);
    }

    [Fact]
    public void ToCipher_NewOrganizationDraft_MapsMultipleCollections()
    {
        var draft = LoginDraft();
        draft.OrganizationId = "org-1";
        draft.ReplaceCollectionIds(["collection-1", "collection-2"]);

        var cipher = CipherDraftMapper.ToCipher(draft, original: null);

        Assert.Equal("org-1", cipher.OrganizationId);
        Assert.Equal(["collection-1", "collection-2"], cipher.CollectionIds);
    }

    [Fact]
    public void ToDraft_ThenToCipher_RoundTripsCardIdentityNoteSsh()
    {
        var ciphers = new[]
        {
            new Cipher { Id = "c", Type = CipherType.Card, Name = "Card", Card = new CipherCard("Jane", "4111", "12", "2030", "123", "Visa") },
            new Cipher { Id = "i", Type = CipherType.Identity, Name = "Id", Identity = new CipherIdentity("Mr", "Jane", null, "Doe", "jdoe", "Acme", "ssn", "pp", "lic", "j@x.com", "123", "Road 1", null, null, "Beijing", null, "100000", "CN") },
            new Cipher { Id = "n", Type = CipherType.SecureNote, Name = "Note", Notes = "body", SecureNote = new CipherSecureNote(0) },
            new Cipher { Id = "s", Type = CipherType.SshKey, Name = "Ssh", Ssh = new CipherSsh("priv", "pub", "SHA256:fp") },
        };

        foreach (var original in ciphers)
        {
            var roundTripped = CipherDraftMapper.ToCipher(CipherDraftMapper.ToDraft(original), original);

            Assert.Equal(original.Type, roundTripped.Type);
            Assert.Equal(original.Name, roundTripped.Name);
            switch (original.Type)
            {
                case CipherType.Card:
                    Assert.Equal(original.Card!.Number, roundTripped.Card!.Number);
                    Assert.Equal(original.Card.Brand, roundTripped.Card.Brand);
                    break;
                case CipherType.Identity:
                    Assert.Equal(original.Identity!.FirstName, roundTripped.Identity!.FirstName);
                    Assert.Equal(original.Identity.Country, roundTripped.Identity.Country);
                    break;
                case CipherType.SecureNote:
                    Assert.Equal(original.Notes, roundTripped.Notes);
                    Assert.NotNull(roundTripped.SecureNote);
                    break;
                case CipherType.SshKey:
                    Assert.Equal(original.Ssh!.PrivateKey, roundTripped.Ssh!.PrivateKey);
                    Assert.Equal(original.Ssh.Fingerprint, roundTripped.Ssh.Fingerprint);
                    break;
            }
        }
    }

    [Fact]
    public void ToDraft_ThenToCipher_RoundTripsNewCipherTypes()
    {
        var ciphers = new[]
        {
            new Cipher { Id = "b", Type = CipherType.BankAccount, Name = "Bank", BankAccount = new CipherBankAccount("Example", "Ada", "Checking", "123", "456", "001", "7890", "SWIFT", "IBAN", "555") },
            new Cipher { Id = "d", Type = CipherType.DriversLicense, Name = "License", DriversLicense = new CipherDriversLicense("Ada", "M", "Lovelace", "1815", "DL-1", "GB", "London", "2025", "2030", "DVLA", "B") },
            new Cipher { Id = "p", Type = CipherType.Passport, Name = "Passport", Passport = new CipherPassport("Lovelace", "Ada", "1815", "F", "London", "British", "GB", "P-1", "P", "N-1", "HMPO", "2025", "2035") },
        };

        var bank = CipherDraftMapper.ToCipher(CipherDraftMapper.ToDraft(ciphers[0]), ciphers[0]);
        var license = CipherDraftMapper.ToCipher(CipherDraftMapper.ToDraft(ciphers[1]), ciphers[1]);
        var passport = CipherDraftMapper.ToCipher(CipherDraftMapper.ToDraft(ciphers[2]), ciphers[2]);

        Assert.Equal("123", bank.BankAccount!.AccountNumber);
        Assert.Equal("DL-1", license.DriversLicense!.LicenseNumber);
        Assert.Equal("P-1", passport.Passport!.PassportNumber);
    }

    [Fact]
    public void HiddenPasswordCipher_CannotEnterOrLeaveAnEditorDraft()
    {
        var protectedCiphers = new Cipher[]
        {
            new()
            {
                Id = "login", Type = CipherType.Login, Name = "Login", CanViewPassword = false,
                Notes = "protected-notes",
                Login = new CipherLogin("user", "password", "totp", []),
                Fields = [new CipherField("Secret", "hidden-custom-value", CipherFieldType.Hidden)],
            },
            new() { Id = "card", Type = CipherType.Card, Name = "Card", CanViewPassword = false, Card = new CipherCard("Ada", "4111111111111111", "12", "2030", "123", "Visa") },
            new() { Id = "identity", Type = CipherType.Identity, Name = "Identity", CanViewPassword = false, Identity = new CipherIdentity(null, "Ada", null, "Lovelace", null, null, "ssn", "passport", "license", null, null, null, null, null, null, null, null, null) },
            new() { Id = "ssh", Type = CipherType.SshKey, Name = "SSH", CanViewPassword = false, Ssh = new CipherSsh("private", "public", "fingerprint") },
            new() { Id = "bank", Type = CipherType.BankAccount, Name = "Bank", CanViewPassword = false, BankAccount = new CipherBankAccount("Example", "Ada", "Checking", "account", "routing", "branch", "pin", "swift", "iban", null) },
            new() { Id = "license", Type = CipherType.DriversLicense, Name = "License", CanViewPassword = false, DriversLicense = new CipherDriversLicense("Ada", null, "Lovelace", "birth", "number", "GB", null, "issued", "expires", null, null) },
            new() { Id = "passport", Type = CipherType.Passport, Name = "Passport", CanViewPassword = false, Passport = new CipherPassport("Lovelace", "Ada", "birth", "F", "London", "British", "GB", "number", "P", "national-id", null, "issued", "expires") },
        };

        foreach (var cipher in protectedCiphers)
        {
            Assert.Throws<OrganizationCipherOperationException>(() => CipherDraftMapper.ToDraft(cipher));
            Assert.Throws<OrganizationCipherOperationException>(() =>
                CipherDraftMapper.ToCipher(CipherEditorDraft.CreateDefault(KindFor(cipher.Type)), cipher));
        }

        Assert.Throws<OrganizationCipherOperationException>(() =>
            CipherDraftMapper.BuildPasswordHistory(
                CipherEditorDraft.CreateDefault(VaultItemKind.Login),
                protectedCiphers[0],
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ToDraft_BooleanField_RestoresBooleanValue()
    {
        var cipher = new Cipher
        {
            Id = "c", Type = CipherType.Login, Name = "n",
            Login = new CipherLogin(null, null, null, System.Array.Empty<CipherLoginUri>()),
            Fields = new[] { new CipherField("Flag", "true", CipherFieldType.Boolean) },
        };

        var draft = CipherDraftMapper.ToDraft(cipher);

        var field = Assert.Single(draft.CustomFields);
        Assert.Equal(CipherEditorFieldType.Boolean, field.Type);
        Assert.True(field.BooleanValue);
    }

    [Fact]
    public void ToDraft_ThenToCipher_PreservesLinkedAndUnknownFields()
    {
        var original = new Cipher
        {
            Id = "c",
            Type = CipherType.Login,
            Name = "n",
            Login = new CipherLogin("user", "password", null, []),
            Fields =
            [
                new CipherField("", null, CipherFieldType.Linked, LinkedId: 101),
                new CipherField("Future", "opaque", CipherFieldType.Unsupported, LinkedId: 999, RawType: 97),
            ],
        };

        var draft = CipherDraftMapper.ToDraft(original);
        var result = CipherDraftMapper.ToCipher(draft, original);

        Assert.Collection(
            draft.CustomFields,
            field =>
            {
                Assert.Equal(CipherEditorFieldType.Linked, field.Type);
                Assert.Equal(101, field.LinkedId);
                Assert.True(field.PreserveWhenNameBlank);
            },
            field =>
            {
                Assert.Equal(CipherEditorFieldType.Unsupported, field.Type);
                Assert.Equal(97, field.RawType);
            });
        Assert.Equal(2, result.Fields.Count);
        Assert.Equal(CipherFieldType.Linked, result.Fields[0].Type);
        Assert.Equal(101, result.Fields[0].LinkedId);
        Assert.Equal(97, result.Fields[1].WireType);
        Assert.Equal("opaque", result.Fields[1].Value);
    }

    [Fact]
    public void ToCipher_PreservesWhitespaceInSecretAndNotesValues()
    {
        var draft = new CipherEditorDraft { Type = VaultItemKind.Login, Name = "  Trim Me  ", Notes = "  keep\n  indent  " };
        draft.Login.Password = "  secret  ";
        draft.Login.Totp = "  otp  ";

        var cipher = CipherDraftMapper.ToCipher(draft, null);

        Assert.Equal("Trim Me", cipher.Name);               // Name 仍 trim
        Assert.Equal("  secret  ", cipher.Login!.Password); // 密码原样保留(不 trim)
        Assert.Equal("  otp  ", cipher.Login.Totp);
        Assert.Equal("  keep\n  indent  ", cipher.Notes);   // 备注原样保留
    }

    private static VaultItemKind KindFor(CipherType type) => type switch
    {
        CipherType.Login => VaultItemKind.Login,
        CipherType.Card => VaultItemKind.Card,
        CipherType.Identity => VaultItemKind.Identity,
        CipherType.SecureNote => VaultItemKind.Note,
        CipherType.SshKey => VaultItemKind.Ssh,
        CipherType.BankAccount => VaultItemKind.BankAccount,
        CipherType.DriversLicense => VaultItemKind.DriversLicense,
        CipherType.Passport => VaultItemKind.Passport,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

public class CipherDraftMapperPasswordHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private static CipherEditorDraft LoginDraft(string password)
    {
        var d = new CipherEditorDraft { Type = VaultItemKind.Login, Name = "A" };
        d.Login.Password = password;
        return d;
    }

    private static Cipher LoginCipher(string password, params PasswordHistoryEntry[] history) => new()
    {
        Id = "1",
        Type = CipherType.Login,
        Name = "A",
        Login = new CipherLogin("u", password, null, Array.Empty<CipherLoginUri>()),
        PasswordHistory = history,
    };

    [Fact]
    public void ChangedPassword_PrependsOld()
    {
        var h = CipherDraftMapper.BuildPasswordHistory(LoginDraft("new"), LoginCipher("old"), Now);
        var e = Assert.Single(h);
        Assert.Equal("old", e.Password);
        Assert.Equal(Now, e.LastUsedDate);
    }

    [Fact]
    public void UnchangedPassword_KeepsHistory()
    {
        var existing = new PasswordHistoryEntry("older", Now.AddDays(-1));
        var h = CipherDraftMapper.BuildPasswordHistory(LoginDraft("same"), LoginCipher("same", existing), Now);
        Assert.Equal(new[] { existing }, h);
    }

    [Fact]
    public void CapsAtFive_NewestFirst()
    {
        var old5 = Enumerable.Range(0, 5).Select(i => new PasswordHistoryEntry($"p{i}", Now.AddDays(-i))).ToArray();
        var h = CipherDraftMapper.BuildPasswordHistory(LoginDraft("new"), LoginCipher("current", old5), Now);
        Assert.Equal(5, h.Count);
        Assert.Equal("current", h[0].Password);   // 前插旧的当前密码
        Assert.Equal("p3", h[4].Password);          // 最旧(p4)被挤出
    }

    [Fact]
    public void NewItem_NoHistory()
    {
        Assert.Empty(CipherDraftMapper.BuildPasswordHistory(LoginDraft("new"), null, Now));
    }

    [Fact]
    public void EmptyOldPassword_NoPrepend()
    {
        Assert.Empty(CipherDraftMapper.BuildPasswordHistory(LoginDraft("new"), LoginCipher(""), Now));
    }

    [Fact]
    public void NonLogin_NoHistory()
    {
        var d = new CipherEditorDraft { Type = VaultItemKind.Card, Name = "C" };
        var orig = new Cipher { Id = "1", Type = CipherType.Card, Name = "C" };
        Assert.Empty(CipherDraftMapper.BuildPasswordHistory(d, orig, Now));
    }
}
