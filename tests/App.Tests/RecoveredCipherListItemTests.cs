using App.ViewModels.Models;
using Core.Enums;
using Core.Models;
using Xunit;

namespace App.Tests;

public class RecoveredCipherListItemTests
{
    [Theory]
    [InlineData(CipherType.Login, "登录")]
    [InlineData(CipherType.SecureNote, "安全笔记")]
    [InlineData(CipherType.Card, "卡片")]
    [InlineData(CipherType.Identity, "身份")]
    [InlineData(CipherType.SshKey, "SSH 密钥")]
    [InlineData(CipherType.BankAccount, "银行账户")]
    [InlineData(CipherType.DriversLicense, "驾驶证")]
    [InlineData(CipherType.Passport, "护照")]
    public void FromCipher_UsesLocalizedTypeLabels(CipherType type, string expected)
    {
        var item = RecoveredCipherListItem.FromCipher(new Cipher
        {
            Name = "Example",
            Type = type,
        });

        Assert.Equal("Example", item.Name);
        Assert.Equal(expected, item.TypeLabel);
    }

    [Fact]
    public void FromCipher_LoginShowsUsernameWithoutCopyingSecrets()
    {
        var cipher = new Cipher
        {
            Name = "Work account",
            Type = CipherType.Login,
            Notes = "secret-note",
            Login = new CipherLogin(
                "alice@example.com",
                "secret-password",
                "secret-totp",
                [new CipherLoginUri("https://private.example/path", null)]),
            Fields = [new CipherField("Recovery", "secret-field", CipherFieldType.Hidden)],
            PasswordHistory = [new PasswordHistoryEntry("old-secret-password", null)],
        };

        var item = RecoveredCipherListItem.FromCipher(cipher);
        var visibleText = VisibleText(item);

        Assert.Equal("登录 · alice@example.com", item.Subtitle);
        Assert.DoesNotContain("secret-password", visibleText);
        Assert.DoesNotContain("secret-totp", visibleText);
        Assert.DoesNotContain("secret-note", visibleText);
        Assert.DoesNotContain("private.example", visibleText);
        Assert.DoesNotContain("secret-field", visibleText);
        Assert.DoesNotContain("old-secret-password", visibleText);
    }

    [Fact]
    public void FromCipher_OtherTypesExposeOnlyAllowlistedSummaries()
    {
        var card = RecoveredCipherListItem.FromCipher(new Cipher
        {
            Name = "Travel card",
            Type = CipherType.Card,
            Card = new CipherCard("Secret Holder", "4111111111111111", "12", "2030", "123", "Visa"),
        });
        var identity = RecoveredCipherListItem.FromCipher(new Cipher
        {
            Name = "Identity",
            Type = CipherType.Identity,
            Identity = new CipherIdentity(
                null, "Alice", null, "Example", "private-user", "Secret Co", "secret-ssn",
                "secret-passport", "secret-license", "secret@example.com", "secret-phone",
                "secret-address", null, null, null, null, null, null),
        });
        var ssh = RecoveredCipherListItem.FromCipher(new Cipher
        {
            Name = "Production key",
            Type = CipherType.SshKey,
            Ssh = new CipherSsh("secret-private-key", "secret-public-key", "SHA256:safe-fingerprint"),
        });
        var note = RecoveredCipherListItem.FromCipher(new Cipher
        {
            Name = "Recovery note",
            Type = CipherType.SecureNote,
            Notes = "secret-note-body",
            SecureNote = new CipherSecureNote(0),
        });
        var bank = RecoveredCipherListItem.FromCipher(new Cipher
        {
            Name = "Checking",
            Type = CipherType.BankAccount,
            BankAccount = new CipherBankAccount(
                "Example Bank", "Secret Name", "Checking", "secret-account", "secret-routing",
                "secret-branch", "secret-pin", "secret-swift", "secret-iban", "secret-phone"),
        });
        var license = RecoveredCipherListItem.FromCipher(new Cipher
        {
            Name = "License",
            Type = CipherType.DriversLicense,
            DriversLicense = new CipherDriversLicense(
                "Secret", null, "Name", "secret-birth-date", "secret-license-number", "US", "CA",
                "secret-issue-date", "secret-expiration", "Secret Authority", "secret-class"),
        });
        var passport = RecoveredCipherListItem.FromCipher(new Cipher
        {
            Name = "Passport",
            Type = CipherType.Passport,
            Passport = new CipherPassport(
                "Secret Surname", "Secret Given", "secret-birth-date", "secret-sex", "secret-place",
                "secret-nationality", "GB", "secret-passport-number", "secret-type", "secret-national-id",
                "Secret Authority", "secret-issue-date", "secret-expiration"),
        });

        Assert.Equal("卡片 · Visa", card.Subtitle);
        Assert.Equal("身份", identity.Subtitle);
        Assert.Equal("SSH 密钥 · SHA256:safe-fingerprint", ssh.Subtitle);
        Assert.Equal("安全笔记", note.Subtitle);
        Assert.Equal("银行账户 · Example Bank", bank.Subtitle);
        Assert.Equal("驾驶证 · US", license.Subtitle);
        Assert.Equal("护照 · GB", passport.Subtitle);

        var visibleText = string.Join(
            "|",
            VisibleText(card),
            VisibleText(identity),
            VisibleText(ssh),
            VisibleText(note),
            VisibleText(bank),
            VisibleText(license),
            VisibleText(passport));
        Assert.DoesNotContain("4111111111111111", visibleText);
        Assert.DoesNotContain("123", visibleText);
        Assert.DoesNotContain("Secret Holder", visibleText);
        Assert.DoesNotContain("private-user", visibleText);
        Assert.DoesNotContain("Secret Co", visibleText);
        Assert.DoesNotContain("secret-ssn", visibleText);
        Assert.DoesNotContain("secret-passport", visibleText);
        Assert.DoesNotContain("secret-license", visibleText);
        Assert.DoesNotContain("secret@example.com", visibleText);
        Assert.DoesNotContain("secret-phone", visibleText);
        Assert.DoesNotContain("secret-address", visibleText);
        Assert.DoesNotContain("secret-private-key", visibleText);
        Assert.DoesNotContain("secret-public-key", visibleText);
        Assert.DoesNotContain("secret-note-body", visibleText);
        Assert.DoesNotContain("secret-account", visibleText);
        Assert.DoesNotContain("secret-routing", visibleText);
        Assert.DoesNotContain("secret-pin", visibleText);
        Assert.DoesNotContain("secret-license-number", visibleText);
        Assert.DoesNotContain("secret-passport-number", visibleText);
        Assert.DoesNotContain("secret-national-id", visibleText);
    }

    [Fact]
    public void FromCipher_UnknownTypeDoesNotExposeEnumValue()
    {
        var item = RecoveredCipherListItem.FromCipher(new Cipher
        {
            Name = "Future item",
            Type = (CipherType)999,
        });

        Assert.Equal("其他条目", item.TypeLabel);
        Assert.Equal("其他条目", item.Subtitle);
        Assert.DoesNotContain("999", VisibleText(item));
    }

    private static string VisibleText(RecoveredCipherListItem item) =>
        string.Join("|", item.Name, item.TypeLabel, item.AccountSummary, item.Subtitle);
}
