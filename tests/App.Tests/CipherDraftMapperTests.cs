using App.Services;
using App.ViewModels.Models;
using Core.Enums;
using Core.Models;
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
        Assert.Equal("New", updated.Name);
        Assert.Equal(original.CreationDate, updated.CreationDate);
        Assert.Equal(original.RevisionDate, updated.RevisionDate);
        Assert.Equal("cred", Assert.Single(updated.Login!.Fido2Credentials).CredentialId);
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
}
