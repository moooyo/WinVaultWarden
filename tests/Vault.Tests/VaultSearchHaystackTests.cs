using Core.Enums;
using Core.Models;
using Vault;
using Xunit;

namespace Vault.Tests;

public class VaultSearchHaystackTests
{
    private static Cipher Login() => new()
    {
        Id = "1", Type = CipherType.Login, Name = "GitHub",
        Notes = "recovery codes here",
        Login = new CipherLogin("octocat", "SUPER-SECRET-PW", "JBSWY3DPEHPK3PXP",
            new[] { new CipherLoginUri("https://github.com/login", 0) }),
        Fields = new[]
        {
            new CipherField("plan tier", "pro", CipherFieldType.Text),
            new CipherField("secret answer", "my-hidden-value", CipherFieldType.Hidden),
            new CipherField("linked password", "linked-secret", CipherFieldType.Linked, LinkedId: 101),
            new CipherField("future field", "future-secret", CipherFieldType.Unsupported, RawType: 97),
        },
    };

    [Fact]
    public void Haystack_Includes_NonSensitive_Fields_Lowercased()
    {
        var h = VaultSearch.BuildHaystack(Login());
        Assert.Contains("github", h);              // name
        Assert.Contains("octocat", h);             // username
        Assert.Contains("github.com/login", h);    // uri
        Assert.Contains("recovery codes", h);      // notes
        Assert.Contains("plan tier", h);           // custom field name
        Assert.Contains("pro", h);                 // custom field non-hidden value
        Assert.Contains("secret answer", h);       // hidden field NAME is searchable
        Assert.Equal(h, h.ToLowerInvariant());     // all lowercased
    }

    [Fact]
    public void Haystack_Excludes_Password_And_HiddenValue()
    {
        var h = VaultSearch.BuildHaystack(Login());
        Assert.DoesNotContain("super-secret-pw", h);   // password never searchable
        Assert.DoesNotContain("my-hidden-value", h);   // hidden field VALUE excluded
        Assert.DoesNotContain("linked-secret", h);
        Assert.DoesNotContain("future-secret", h);
    }

    [Fact]
    public void Haystack_Card_Last4_Only_Not_Full_Or_Cvv()
    {
        var c = new Cipher { Id = "2", Type = CipherType.Card, Name = "Visa",
            Card = new CipherCard("Zhang San", "4111111111119999", "08", "28", "321", "Visa") };
        var h = VaultSearch.BuildHaystack(c);
        Assert.Contains("9999", h);                 // last 4
        Assert.DoesNotContain("4111111111119999", h); // not the full number
        Assert.DoesNotContain("321", h);            // CVV never searchable
    }

    [Fact]
    public void Haystack_Identity_Fields()
    {
        var c = new Cipher { Id = "3", Type = CipherType.Identity, Name = "Me",
            Identity = new CipherIdentity("Mr", "Ada", null, "Lovelace", "ada", "ACME",
                null, null, null, "ada@example.com", "555-1234", null, null, null, "London", null, null, null) };
        var h = VaultSearch.BuildHaystack(c);
        Assert.Contains("ada", h);
        Assert.Contains("lovelace", h);
        Assert.Contains("ada@example.com", h);
        Assert.Contains("555-1234", h);
    }

    [Fact]
    public void Haystack_HiddenPasswordsExcludeProtectedNotesAndNumberFragments()
    {
        var cipher = new Cipher
        {
            Id = "hidden",
            Type = CipherType.Login,
            Name = "Visible name",
            Notes = "protected-note",
            CanViewPassword = false,
            Login = new CipherLogin("visible-user", "protected-password", "protected-totp", []),
            Card = new CipherCard(null, "4111111111111111", null, null, "123", null),
            BankAccount = new CipherBankAccount("Visible bank", null, null, "account-2222", null, null, null, null, "iban-3333", null),
            DriversLicense = new CipherDriversLicense("Visible", null, "Driver", null, "license-4444", "GB", null, null, null, null, null),
            Passport = new CipherPassport("Person", "Visible", null, null, null, null, "GB", "passport-5555", null, null, null, null, null),
        };

        var haystack = VaultSearch.BuildHaystack(cipher);

        Assert.Contains("visible name", haystack);
        Assert.Contains("visible-user", haystack);
        Assert.Contains("visible bank", haystack);
        Assert.DoesNotContain("protected-note", haystack);
        Assert.DoesNotContain("1111", haystack);
        Assert.DoesNotContain("2222", haystack);
        Assert.DoesNotContain("3333", haystack);
        Assert.DoesNotContain("4444", haystack);
        Assert.DoesNotContain("5555", haystack);
    }
}
