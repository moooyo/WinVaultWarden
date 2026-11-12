using App.Services;
using Core.Enums;
using Core.Models;
using Crypto;
using Vault;
using Xunit;

namespace App.Tests;

public sealed class RecoveredVaultViewSessionTests
{
    [Fact]
    public void LoginSecrets_AreMaskedUntilReveal_AndCopyUsesProtectedClipboard()
    {
        using var context = new Context(Login());
        Assert.True(context.View.Select(0));
        var password = Assert.Single(context.View.Fields, field => field.Label == Caption("Password"));
        Assert.Equal(RecoveredCipherField.SecretMask, password.DisplayValue);
        Assert.DoesNotContain("recovered-password", password.ToString());

        Assert.True(context.View.Copy(password));
        Assert.Equal("recovered-password", context.Clipboard.Text);
        Assert.True(context.Clipboard.WasSecret);
        Assert.Equal(RecoveredCipherField.SecretMask, password.DisplayValue);
        Assert.True(context.View.ToggleReveal(password));
        Assert.Equal("recovered-password", password.DisplayValue);
        Assert.True(context.View.ToggleReveal(password));
        Assert.Equal(RecoveredCipherField.SecretMask, password.DisplayValue);

        var username = Assert.Single(context.View.Fields, field => field.Label == Caption("Username"));
        Assert.True(context.View.Copy(username));
        Assert.Equal("recovered-user", context.Clipboard.Text);
        Assert.False(context.Clipboard.WasSecret);
    }

    [Fact]
    public async Task Reprompt_HidesAllFieldsUntilVerificationSucceeds()
    {
        using var context = new Context(Login(reprompt: true));
        Assert.True(context.View.Select(0));
        Assert.True(context.View.RequiresVerification);
        Assert.Empty(context.View.Fields);

        Assert.False(await context.View.VerifySelectionAsync(_ => Task.FromResult(false)));
        Assert.Empty(context.View.Fields);
        Assert.True(await context.View.VerifySelectionAsync(_ => Task.FromResult(true)));
        Assert.False(context.View.RequiresVerification);
        Assert.NotEmpty(context.View.Fields);
        Assert.Equal(RecoveredCipherField.SecretMask,
            Assert.Single(context.View.Fields, field => field.Label == Caption("Password")).DisplayValue);
    }

    [Fact]
    public async Task DelayedVerification_CannotAuthorizeAnotherSelection()
    {
        using var context = new Context(Login(reprompt: true), Login("second", reprompt: true));
        Assert.True(context.View.Select(0));
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifying = context.View.VerifySelectionAsync(_ => response.Task);
        Assert.True(context.View.Select(1));
        response.SetResult(true);

        Assert.False(await verifying);
        Assert.True(context.View.RequiresVerification);
        Assert.Empty(context.View.Fields);
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("switch")]
    [InlineData("dispose")]
    public void EndingScope_ClearsProjectedValues_AndRejectsRetainedActions(string transition)
    {
        using var context = new Context(Login());
        Assert.True(context.View.Select(0));
        var retained = context.View.Fields.ToArray();
        var password = Assert.Single(retained, field => field.Label == Caption("Password"));
        Assert.True(context.View.ToggleReveal(password));
        switch (transition)
        {
            case "lock":
                context.Session.Lock();
                break;
            case "switch":
                context.Session.BeginAccountSession("other", "other@example.com", new ServerOrigin("https://other.example"));
                context.Session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
                break;
            default:
                context.View.Dispose();
                break;
        }

        Assert.Empty(context.View.Items);
        Assert.Empty(context.View.Fields);
        Assert.All(retained, field => Assert.Equal(string.Empty, field.DisplayValue));
        Assert.False(context.View.Copy(password));
        Assert.False(context.View.ToggleReveal(password));
        Assert.False(context.View.Select(0));
        Assert.Null(context.Clipboard.Text);
    }

    [Fact]
    public void SelectionChange_ClearsOldFields_AndRejectsQueuedCopyFromOldRow()
    {
        using var context = new Context(Login(), Login("second"));
        Assert.True(context.View.Select(0));
        var oldPassword = Assert.Single(context.View.Fields, field => field.Label == Caption("Password"));
        Assert.True(context.View.ToggleReveal(oldPassword));
        Assert.True(context.View.Select(1));

        Assert.Equal(string.Empty, oldPassword.DisplayValue);
        Assert.False(context.View.Copy(oldPassword));
        Assert.False(context.View.ToggleReveal(oldPassword));
        Assert.Null(context.Clipboard.Text);
    }

    [Fact]
    public async Task SessionEndingDuringVerification_DropsSuccessfulAnswer()
    {
        using var context = new Context(Login(reprompt: true));
        Assert.True(context.View.Select(0));
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifying = context.View.VerifySelectionAsync(_ => response.Task);
        context.Session.Lock();
        response.SetResult(true);
        Assert.False(await verifying);
        Assert.Empty(context.View.Fields);
    }

    [Fact]
    public void CurrentPasswordPermission_IsPreservedForRecoveredItems()
    {
        using var context = new Context(new Cipher
        {
            Type = CipherType.Login,
            CanViewPassword = false,
            Login = new CipherLogin("recipient-visible", "forbidden-password", "forbidden-seed", []),
            Notes = "forbidden-notes",
            Fields = [new CipherField("Hidden", "forbidden-custom", CipherFieldType.Hidden)],
            PasswordHistory = [new PasswordHistoryEntry("forbidden-history", null)],
        });
        Assert.True(context.View.Select(0));
        var field = Assert.Single(context.View.Fields);
        Assert.Equal(Caption("Username"), field.Label);
        Assert.Equal("recipient-visible", field.DisplayValue);
    }

    [Fact]
    public void CustomFieldsAndPasswordHistory_RemainUsableWithoutExposingPrivatePasskeyMaterial()
    {
        using var context = new Context(new Cipher
        {
            Type = CipherType.Login,
            Notes = "private-note",
            Login = new CipherLogin("u", "p", null, [])
            {
                Fido2Credentials = [new CipherFido2Credential(
                    "id", "type", "algorithm", "curve", "never-project-private-key",
                    "rp.example", "handle", "user", 0, "RP", "Display", true, null)],
            },
            Fields =
            [
                new CipherField("Public custom", "visible-custom", CipherFieldType.Text),
                new CipherField("Hidden custom", "hidden-custom", CipherFieldType.Hidden),
                new CipherField("Linked password", null, CipherFieldType.Linked, 101),
            ],
            PasswordHistory = [new PasswordHistoryEntry("old-password", null)],
            Attachments = [new CipherAttachment("attachment", "backup.pdf", 1, "1 B")],
        });
        Assert.True(context.View.Select(0));
        Assert.Equal("visible-custom", Assert.Single(context.View.Fields, field => field.Label == "Public custom").DisplayValue);
        var linked = Assert.Single(context.View.Fields, field => field.Label == "Linked password");
        Assert.Equal(RecoveredCipherField.SecretMask, linked.DisplayValue);
        Assert.True(context.View.Copy(linked));
        Assert.Equal("p", context.Clipboard.Text);
        Assert.Contains(context.View.Fields, field => field.Label == Caption("Previous password") && field.IsSecret);
        Assert.Contains(context.View.Fields, field => field.Label == Caption("Attachment filename") && field.DisplayValue == "backup.pdf");
        Assert.DoesNotContain(context.View.Fields, field => field.DisplayValue.Contains("never-project-private-key", StringComparison.Ordinal));
    }

    [Fact]
    public void EachSupportedCipherType_ProjectsItsUsableValues()
    {
        var examples = new[]
        {
            (new Cipher { Type = CipherType.SecureNote, Notes = "note-content" }, "Notes", "note-content"),
            (new Cipher { Type = CipherType.Card, Card = new CipherCard("holder", "1234", "1", "2030", "123", "Visa") }, "Card number", "1234"),
            (new Cipher { Type = CipherType.Identity, Identity = new CipherIdentity(null, "First", null, null, null, null, "ssn", null, null, null, null, null, null, null, null, null, null, null) }, "Social security number", "ssn"),
            (new Cipher { Type = CipherType.SshKey, Ssh = new CipherSsh("private-key", "public-key", "fingerprint") }, "Private key", "private-key"),
            (new Cipher { Type = CipherType.BankAccount, BankAccount = new CipherBankAccount("bank", "holder", "checking", "account", "routing", null, null, null, null, null) }, "Account number", "account"),
            (new Cipher { Type = CipherType.DriversLicense, DriversLicense = new CipherDriversLicense(null, null, null, null, "license", null, null, null, null, null, null) }, "License number", "license"),
            (new Cipher { Type = CipherType.Passport, Passport = new CipherPassport(null, null, null, null, null, null, null, "passport", null, null, null, null, null) }, "Passport number", "passport"),
        };
        foreach (var (cipher, label, expected) in examples)
        {
            using var context = new Context(cipher);
            Assert.True(context.View.Select(0));
            var field = Assert.Single(context.View.Fields, field => field.Label == Caption(label));
            Assert.True(context.View.Copy(field));
            Assert.Equal(expected, context.Clipboard.Text);
            Assert.True(context.Clipboard.WasSecret);
        }
    }

    private static string Caption(string englishCaption) =>
        LocalizedSourceAssert.EnglishCaptionInDefaultLanguage(englishCaption);

    private static Cipher Login(string id = "first", bool reprompt = false) => new()
    {
        Id = id,
        Type = CipherType.Login,
        Name = $"Recovered {id}",
        Reprompt = reprompt,
        Login = new CipherLogin("recovered-user", "recovered-password", null, []),
    };

    private sealed class Context : IDisposable
    {
        public VaultSession Session { get; } = new();
        public Clipboard Clipboard { get; } = new();
        public RecoveredVaultViewSession View { get; }
        public Context(params Cipher[] ciphers)
        {
            var lease = Session.BeginAccountSession("recipient", "recipient@example.com", new ServerOrigin("https://vault.example"));
            Session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
            View = new(Session, lease, new RecoveredVault("grantor@example.com", ciphers), Clipboard);
        }
        public void Dispose() { View.Dispose(); Session.Lock(); }
    }

    private sealed class Clipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public bool WasSecret { get; private set; }
        public void SetText(string text) { Text = text; WasSecret = false; }
        public void SetSecretText(string text, int? autoClearSeconds = null) { Text = text; WasSecret = true; }
    }
}
