using Core.Enums;
using Core.Models;
using Core.Services;
using Xunit;

namespace Vault.Tests;

public class PasswordGeneratorPolicyResolverTests
{
    [Fact]
    public void Resolve_MergesStrictestRequirementsAcrossOwnerAndAdminOrganizations()
    {
        var organizations = new[]
        {
            Context("owner-org", role: 0),
            Context("admin-org", role: 1),
        };
        var policies = new[]
        {
            Policy(
                "owner-policy",
                "owner-org",
                """{"minLength":20,"useUpper":true,"minNumbers":3,"minNumberWords":5,"capitalize":true,"overridePasswordType":"passphrase"}"""),
            Policy(
                "admin-policy",
                "admin-org",
                """{"minLength":24,"useLower":true,"useNumbers":true,"useSpecial":true,"minSpecial":4,"minNumberWords":7,"includeNumber":true}"""),
        };

        var result = PasswordGeneratorPolicyResolver.Resolve(organizations, policies);

        Assert.Equal(2, result.ApplicablePolicyCount);
        Assert.False(result.HasMalformedPolicyData);
        Assert.Equal(24, result.MinLength);
        Assert.True(result.UseUppercase);
        Assert.True(result.UseLowercase);
        Assert.True(result.UseNumbers);
        Assert.Equal(3, result.MinNumbers);
        Assert.True(result.UseSpecial);
        Assert.Equal(4, result.MinSpecial);
        Assert.Equal(7, result.MinNumberWords);
        Assert.True(result.Capitalize);
        Assert.True(result.IncludeNumber);
        Assert.Equal(
            PasswordGeneratorCredentialType.Passphrase,
            result.OverridePasswordType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_PasswordOverrideWinsRegardlessOfPolicyOrder(bool passwordFirst)
    {
        var password = Policy(
            "password-policy",
            "org-a",
            """{"overridePasswordType":"password"}""");
        var passphrase = Policy(
            "passphrase-policy",
            "org-b",
            """{"overridePasswordType":"passphrase"}""");
        var policies = passwordFirst
            ? new[] { password, passphrase }
            : new[] { passphrase, password };

        var result = PasswordGeneratorPolicyResolver.Resolve(
            [Context("org-a", 2), Context("org-b", 2)],
            policies);

        Assert.Equal(
            PasswordGeneratorCredentialType.Password,
            result.OverridePasswordType);
    }

    [Fact]
    public void Resolve_IgnoresDisabledUnrelatedAndProviderPolicies()
    {
        var organizations = new[]
        {
            Context("member-org", role: 2),
            Context("provider-org", role: 2, isProviderUser: true),
        };
        var policies = new[]
        {
            Policy("disabled", "member-org", """{"minLength":40}""", enabled: false),
            new VaultOrganizationPolicy(
                "unrelated",
                "member-org",
                (int)OrganizationPolicyType.DisableSend,
                true,
                """{"minLength":50}"""),
            Policy("provider", "provider-org", """{"minLength":60}"""),
        };

        var result = PasswordGeneratorPolicyResolver.Resolve(organizations, policies);

        Assert.Equal(0, result.ApplicablePolicyCount);
        Assert.False(result.InEffect);
        Assert.False(result.HasMalformedPolicyData);
    }

    [Fact]
    public void Resolve_MalformedPolicyDoesNotHideValidPolicyRequirements()
    {
        var organizations = new[]
        {
            Context("org-a", 2),
            Context("org-b", 2),
            Context("org-c", 2),
        };
        var policies = new[]
        {
            Policy("invalid-json", "org-a", "not-json"),
            Policy(
                "invalid-fields",
                "org-b",
                """{"minLength":"64","minNumbers":129,"useUpper":"yes","useLower":true,"overridePasswordType":"pin"}"""),
            Policy(
                "valid",
                "org-c",
                """{"minLength":28,"useSpecial":true,"minSpecial":5}"""),
        };

        var result = PasswordGeneratorPolicyResolver.Resolve(organizations, policies);

        Assert.Equal(3, result.ApplicablePolicyCount);
        Assert.True(result.HasMalformedPolicyData);
        Assert.Equal(28, result.MinLength);
        Assert.True(result.UseLowercase);
        Assert.False(result.UseUppercase);
        Assert.True(result.UseSpecial);
        Assert.Equal(5, result.MinSpecial);
        Assert.Null(result.OverridePasswordType);
        Assert.False(result.CanGenerateCompliantCredential);
    }

    [Fact]
    public void Resolve_WrongCaseKnownFieldFailsClosedInsteadOfBeingIgnored()
    {
        var result = PasswordGeneratorPolicyResolver.Resolve(
            [Context("org-a", 2)],
            [Policy("policy-a", "org-a", """{"UseUpper":true}""")]);

        Assert.True(result.HasMalformedPolicyData);
        Assert.False(result.CanGenerateCompliantCredential);
    }

    [Fact]
    public void Resolve_OfficialDoubleDigitCharacterMinimumIsPreserved()
    {
        var result = PasswordGeneratorPolicyResolver.Resolve(
            [Context("org-a", 2)],
            [Policy("policy-a", "org-a", """{"minNumbers":10,"minSpecial":11}""")]);

        Assert.False(result.HasMalformedPolicyData);
        Assert.Equal(10, result.MinNumbers);
        Assert.Equal(11, result.MinSpecial);
        Assert.True(result.PasswordRequirementsAreSatisfiable);
    }

    [Fact]
    public void Resolve_ImpossibleCombinedMinimumIsReportedAsMalformed()
    {
        var result = PasswordGeneratorPolicyResolver.Resolve(
            [Context("org-a", 2), Context("org-b", 2)],
            [
                Policy("policy-a", "org-a", """{"minNumbers":128}"""),
                Policy("policy-b", "org-b", """{"minSpecial":128}"""),
            ]);

        Assert.True(result.HasMalformedPolicyData);
        Assert.False(result.PasswordRequirementsAreSatisfiable);
    }

    [Fact]
    public void Resolve_MissingMembershipContextFailsClosed()
    {
        var policy = Policy(
            "orphan-policy",
            "missing-org",
            """{"minLength":32,"useNumbers":true}""");

        var result = PasswordGeneratorPolicyResolver.Resolve(
            Array.Empty<VaultOrganizationPolicyContext>(),
            [policy]);

        Assert.Equal(1, result.ApplicablePolicyCount);
        Assert.Equal(32, result.MinLength);
        Assert.True(result.UseNumbers);
    }

    [Fact]
    public void Resolve_NullPolicyDataIsAnInactiveValidPolicy()
    {
        var result = PasswordGeneratorPolicyResolver.Resolve(
            [Context("org-a", 2)],
            [Policy("empty", "org-a", "null")]);

        Assert.Equal(1, result.ApplicablePolicyCount);
        Assert.False(result.HasMalformedPolicyData);
        Assert.False(result.InEffect);
    }

    private static VaultOrganizationPolicyContext Context(
        string organizationId,
        int role,
        bool isProviderUser = false) =>
        new(
            organizationId,
            Status: 2,
            Role: role,
            Enabled: true,
            UsePolicies: true,
            IsProviderUser: isProviderUser);

    private static VaultOrganizationPolicy Policy(
        string id,
        string organizationId,
        string dataJson,
        bool enabled = true) =>
        new(
            id,
            organizationId,
            (int)OrganizationPolicyType.PasswordGenerator,
            enabled,
            dataJson);
}
