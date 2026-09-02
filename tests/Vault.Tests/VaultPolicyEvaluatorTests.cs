using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class VaultPolicyEvaluatorTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    public void IsEnabledForUser_UsesMembershipRole(int role, bool expected)
    {
        var session = SessionWithPolicy(role, OrganizationPolicyType.DisableSend);

        Assert.Equal(
            expected,
            VaultPolicyEvaluator.IsEnabledForUser(session, OrganizationPolicyType.DisableSend));
    }

    [Theory]
    [InlineData(OrganizationPolicyType.MasterPassword)]
    [InlineData(OrganizationPolicyType.PasswordGenerator)]
    [InlineData(OrganizationPolicyType.RemoveUnlockWithPin)]
    [InlineData(OrganizationPolicyType.RestrictedItemTypes)]
    public void IsEnabledForUser_NonExemptPoliciesApplyToOwnersAndAdmins(
        OrganizationPolicyType policyType)
    {
        Assert.True(VaultPolicyEvaluator.IsEnabledForUser(
            SessionWithPolicy(0, policyType),
            policyType));
        Assert.True(VaultPolicyEvaluator.IsEnabledForUser(
            SessionWithPolicy(1, policyType),
            policyType));
    }

    [Theory]
    [InlineData(1, true, false, true)]
    [InlineData(2, true, false, true)]
    [InlineData(0, true, false, false)]
    [InlineData(-1, true, false, false)]
    [InlineData(2, false, false, false)]
    [InlineData(2, true, true, false)]
    public void IsEnabledForUser_UsesOfficialMembershipContext(
        int status,
        bool usePolicies,
        bool isProviderUser,
        bool expected)
    {
        var contexts = new[]
        {
            new VaultOrganizationPolicyContext(
                "org-1",
                status,
                Role: 2,
                Enabled: true,
                UsePolicies: usePolicies,
                IsProviderUser: isProviderUser),
        };
        var policies = new[]
        {
            new VaultOrganizationPolicy(
                "policy-1",
                "org-1",
                (int)OrganizationPolicyType.DisableSend,
                true,
                "null"),
        };

        Assert.Equal(
            expected,
            VaultPolicyEvaluator.IsEnabledForUser(
                OrganizationPolicyType.DisableSend,
                contexts,
                policies));
    }

    [Fact]
    public void IsEnabledForUser_MissingOrAmbiguousContextFailsClosed()
    {
        var policy = new VaultOrganizationPolicy(
            "policy-1",
            "org-1",
            (int)OrganizationPolicyType.DisableSend,
            true,
            "null");

        Assert.True(VaultPolicyEvaluator.IsEnabledForUser(
            OrganizationPolicyType.DisableSend,
            Array.Empty<VaultOrganizationPolicyContext>(),
            [policy]));
        Assert.True(VaultPolicyEvaluator.IsEnabledForUser(
            OrganizationPolicyType.DisableSend,
            [
                new VaultOrganizationPolicyContext("org-1", 2, 2, true, true, false),
                new VaultOrganizationPolicyContext("ORG-1", 2, 2, true, true, false),
            ],
            [policy]));
    }

    [Fact]
    public void IsEnabledForUser_IgnoresDisabledPolicyAndOrganization()
    {
        var session = NewSession();
        var snapshot = new DecryptedVault(session.Account, [], [], 0, 0)
        {
            Organizations = [new VaultOrganization("org-1", "Team", 2, 2, false)],
            Policies = [new VaultOrganizationPolicy("p-1", "org-1", 6, true, "{}")],
        };
        session.SetSnapshot(snapshot);

        Assert.False(VaultPolicyEvaluator.IsEnabledForUser(
            session,
            OrganizationPolicyType.DisableSend));
    }

    [Fact]
    public void IsCipherRestricted_RestrictsPersonalItemsButAllowsOtherOrganizationItems()
    {
        var organizations = new[]
        {
            new VaultOrganization("restricting-org", "Restricted", 2, 2, true),
            new VaultOrganization("other-org", "Other", 2, 2, true),
        };
        var policies = new[]
        {
            new VaultOrganizationPolicy(
                "policy-1",
                "restricting-org",
                (int)OrganizationPolicyType.RestrictedItemTypes,
                true,
                "[3]"),
        };
        var personalCard = new Cipher { Type = CipherType.Card, Name = "Personal card" };
        var restrictedCard = new Cipher
        {
            Type = CipherType.Card,
            OrganizationId = "restricting-org",
            Name = "Restricted card",
        };
        var allowedCard = new Cipher
        {
            Type = CipherType.Card,
            OrganizationId = "other-org",
            Name = "Allowed card",
        };

        Assert.True(VaultPolicyEvaluator.IsCipherRestricted(personalCard, organizations, policies));
        Assert.True(VaultPolicyEvaluator.IsCipherRestricted(restrictedCard, organizations, policies));
        Assert.False(VaultPolicyEvaluator.IsCipherRestricted(allowedCard, organizations, policies));
    }

    [Fact]
    public void IsCipherRestricted_InvalidPolicyDataDefaultsToCard()
    {
        var organizations = new[] { new VaultOrganization("org-1", "Team", 2, 2, true) };
        var policies = new[]
        {
            new VaultOrganizationPolicy(
                "policy-1",
                "org-1",
                (int)OrganizationPolicyType.RestrictedItemTypes,
                true,
                "not-json"),
        };

        Assert.True(VaultPolicyEvaluator.IsCipherRestricted(
            new Cipher { Type = CipherType.Card, Name = "Card" },
            organizations,
            policies));
        Assert.False(VaultPolicyEvaluator.IsCipherRestricted(
            new Cipher { Type = CipherType.Login, Name = "Login" },
            organizations,
            policies));
    }

    private static VaultSession SessionWithPolicy(int role, OrganizationPolicyType policyType)
    {
        var session = NewSession();
        var snapshot = new DecryptedVault(session.Account, [], [], 0, 0)
        {
            Organizations = [new VaultOrganization("org-1", "Team", 2, role, true)],
            Policies = [new VaultOrganizationPolicy("p-1", "org-1", (int)policyType, true, "{}")],
        };
        session.SetSnapshot(snapshot);
        return session;
    }

    private static VaultSession NewSession()
    {
        var session = new VaultSession();
        session.BeginAccountSession(
            "account-a",
            "me@example.com",
            new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        session.SetSnapshot(new DecryptedVault(
            new AccountInfo(
                "account-a",
                "me@example.com",
                new ServerOrigin("https://vault.example"),
                "M",
                string.Empty),
            [],
            [],
            0,
            0));
        return session;
    }
}
