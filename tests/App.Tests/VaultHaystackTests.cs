using App.Services;
using Core.Enums;
using Core.Models;
using Xunit;

namespace App.Tests;

public class VaultHaystackTests
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
        },
    };

    [Fact]
    public void Haystack_Includes_NonSensitive_Fields_Lowercased()
    {
        var h = VaultUiService.BuildHaystack(Login());
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
        var h = VaultUiService.BuildHaystack(Login());
        Assert.DoesNotContain("super-secret-pw", h);   // password never searchable
        Assert.DoesNotContain("my-hidden-value", h);   // hidden field VALUE excluded
    }

    [Fact]
    public void Haystack_Card_Last4_Only_Not_Full_Or_Cvv()
    {
        var c = new Cipher { Id = "2", Type = CipherType.Card, Name = "Visa",
            Card = new CipherCard("Zhang San", "4111111111119999", "08", "28", "321", "Visa") };
        var h = VaultUiService.BuildHaystack(c);
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
        var h = VaultUiService.BuildHaystack(c);
        Assert.Contains("ada", h);
        Assert.Contains("lovelace", h);
        Assert.Contains("ada@example.com", h);
        Assert.Contains("555-1234", h);
    }
}
