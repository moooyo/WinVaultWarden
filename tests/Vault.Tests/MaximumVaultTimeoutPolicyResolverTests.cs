using Core.Models;
using Core.Services;
using Xunit;

namespace Vault.Tests;

public sealed class MaximumVaultTimeoutPolicyResolverTests
{
    [Fact]
    public void Resolve_UsesStrictestApplicablePolicyAcrossOrganizations()
    {
        var organizations = new[]
        {
            Context("member-org", role: 2),
            Context("admin-org", role: 1),
            Context("owner-org", role: 0),
        };
        var policies = new[]
        {
            Policy("member-org", "{\"minutes\":60,\"action\":\"lock\"}"),
            Policy("admin-org", "{\"type\":\"custom\",\"minutes\":5,\"action\":\"logOut\"}"),
            Policy("owner-org", "{\"minutes\":1}"),
        };

        var result = MaximumVaultTimeoutPolicyResolver.Resolve(organizations, policies);

        Assert.True(result.Applies);
        Assert.Equal(MaximumVaultTimeoutMode.Minutes, result.Mode);
        Assert.Equal(5, result.Minutes);
        Assert.Equal(MaximumVaultTimeoutAction.Logout, result.RequiredAction);
        Assert.False(result.UsedFailClosedFallback);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Resolve_ExemptsOwnersButAppliesToAdminsAndUsers(int role, bool expected)
    {
        var result = MaximumVaultTimeoutPolicyResolver.Resolve(
            [Context("org", role)],
            [Policy("org", "{\"minutes\":30}")]);

        Assert.Equal(expected, result.Applies);
    }

    [Fact]
    public void Resolve_MissingMembershipContextFailsClosed()
    {
        var result = MaximumVaultTimeoutPolicyResolver.Resolve(
            Array.Empty<VaultOrganizationPolicyContext>(),
            [Policy("missing-org", "{\"minutes\":30}")]);

        Assert.True(result.Applies);
        Assert.Equal(MaximumVaultTimeoutMode.Minutes, result.Mode);
        Assert.Equal(30, result.Minutes);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"minutes\":0}")]
    [InlineData("{\"type\":\"unsupported\",\"minutes\":0}")]
    [InlineData("{\"Type\":\"never\",\"minutes\":30}")]
    [InlineData("{\"minutes\":5,\"minutes\":30}")]
    public void Resolve_MalformedDataUsesNonImmediateFailClosedFallback(string dataJson)
    {
        var result = MaximumVaultTimeoutPolicyResolver.Resolve(
            [Context("org", role: 2)],
            [Policy("org", dataJson)]);

        Assert.True(result.Applies);
        Assert.Equal(MaximumVaultTimeoutMode.Minutes, result.Mode);
        Assert.Equal(MaximumVaultTimeoutPolicyResolver.FailClosedFallbackMinutes, result.Minutes);
        Assert.True(result.UsedFailClosedFallback);
    }

    [Fact]
    public void Resolve_ValidImmediatelyPolicyRemainsImmediate()
    {
        var result = MaximumVaultTimeoutPolicyResolver.Resolve(
            [Context("org", role: 2)],
            [Policy("org", "{\"type\":\"immediately\",\"minutes\":0}")]);

        Assert.Equal(MaximumVaultTimeoutMode.Immediately, result.Mode);
        Assert.False(result.UsedFailClosedFallback);
    }

    [Fact]
    public void Resolve_DisabledOrIneligibleMembershipDoesNotApply()
    {
        var result = MaximumVaultTimeoutPolicyResolver.Resolve(
            [new VaultOrganizationPolicyContext("org", 2, 2, true, false, false)],
            [Policy("org", "{\"minutes\":5}")]);

        Assert.False(result.Applies);
    }

    private static VaultOrganizationPolicyContext Context(string id, int role) =>
        new(
            Id: id,
            Status: 2,
            Role: role,
            Enabled: true,
            UsePolicies: true,
            IsProviderUser: false);

    private static VaultOrganizationPolicy Policy(string organizationId, string dataJson) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            OrganizationId: organizationId,
            Type: 9,
            Enabled: true,
            DataJson: dataJson);
}
