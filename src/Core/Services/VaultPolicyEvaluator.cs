using Core.Enums;
using Core.Models;
using Core.Session;
using System.Text.Json;

namespace Core.Services;

public static class VaultPolicyEvaluator
{
    public static bool IsEnabledForUser(
        IVaultSnapshot snapshot,
        OrganizationPolicyType policyType)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return IsEnabledForUser(policyType, snapshot.PolicyOrganizations, snapshot.Policies);
    }

    public static bool IsEnabledForUser(
        OrganizationPolicyType policyType,
        IReadOnlyList<VaultOrganization> organizations,
        IReadOnlyList<VaultOrganizationPolicy> policies) =>
        IsEnabledForUser(policyType, ToPolicyContexts(organizations), policies);

    public static bool IsEnabledForUser(
        OrganizationPolicyType policyType,
        IReadOnlyList<VaultOrganizationPolicyContext> organizations,
        IReadOnlyList<VaultOrganizationPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(organizations);
        ArgumentNullException.ThrowIfNull(policies);
        foreach (var policy in policies)
        {
            if (!policy.Enabled || policy.Type != (int)policyType)
                continue;

            if (PolicyApplies(policy, policyType, organizations))
                return true;
        }

        return false;
    }

    public static void ThrowIfEnabled(
        IVaultSnapshot snapshot,
        OrganizationPolicyType policyType,
        string message)
    {
        if (IsEnabledForUser(snapshot, policyType))
            throw new OrganizationPolicyException(message);
    }

    public static bool IsCipherRestricted(IVaultSnapshot snapshot, Cipher cipher) =>
        IsCipherRestricted(cipher, snapshot.PolicyOrganizations, snapshot.Policies);

    public static bool IsCipherRestricted(
        Cipher cipher,
        IReadOnlyList<VaultOrganization> organizations,
        IReadOnlyList<VaultOrganizationPolicy> policies) =>
        IsCipherRestricted(cipher, ToPolicyContexts(organizations), policies);

    public static bool IsCipherRestricted(
        Cipher cipher,
        IReadOnlyList<VaultOrganizationPolicyContext> organizations,
        IReadOnlyList<VaultOrganizationPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        var applicablePolicies = policies
            .Where(policy => policy.Enabled
                && policy.Type == (int)OrganizationPolicyType.RestrictedItemTypes
                && PolicyApplies(
                    policy,
                    OrganizationPolicyType.RestrictedItemTypes,
                    organizations))
            .ToArray();
        if (applicablePolicies.Length == 0)
            return false;

        var restrictedSomewhere = applicablePolicies.Any(policy =>
            ParseRestrictedTypes(policy.DataJson).Contains((int)cipher.Type));
        if (!restrictedSomewhere)
            return false;

        if (string.IsNullOrWhiteSpace(cipher.OrganizationId))
            return true;

        var owningPolicy = applicablePolicies.FirstOrDefault(policy =>
            string.Equals(
                policy.OrganizationId,
                cipher.OrganizationId,
                StringComparison.OrdinalIgnoreCase));
        return owningPolicy is not null
            && ParseRestrictedTypes(owningPolicy.DataJson).Contains((int)cipher.Type);
    }

    internal static bool PolicyApplies(
        VaultOrganizationPolicy policy,
        OrganizationPolicyType policyType,
        IReadOnlyList<VaultOrganizationPolicyContext> organizations)
    {
        var matches = organizations
            .Where(candidate => string.Equals(
                candidate.Id,
                policy.OrganizationId,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();

        // A policy without one unambiguous membership context is enforced by default, matching
        // the official policy SDK's fail-closed decision.
        if (matches.Length != 1)
            return true;

        var organization = matches[0];
        if (!organization.Enabled
            || !organization.UsePolicies
            || organization.Status is not 1 and not 2
            || organization.IsProviderUser)
        {
            return false;
        }

        return !IsRoleExempt(policyType, organization.Role);
    }

    private static bool IsRoleExempt(OrganizationPolicyType policyType, int role)
    {
        if (policyType == OrganizationPolicyType.MaximumVaultTimeout)
            return role == 0;

        return role is 0 or 1 && policyType switch
        {
            OrganizationPolicyType.MasterPassword
                or OrganizationPolicyType.PasswordGenerator
                or OrganizationPolicyType.RemoveUnlockWithPin
                or OrganizationPolicyType.RestrictedItemTypes
                or OrganizationPolicyType.AutomaticUserConfirmation
                or OrganizationPolicyType.OrganizationUserNotification
                or OrganizationPolicyType.FillAssist => false,
            _ => true,
        };
    }

    private static IReadOnlyList<VaultOrganizationPolicyContext> ToPolicyContexts(
        IReadOnlyList<VaultOrganization> organizations) =>
        organizations.Select(organization => new VaultOrganizationPolicyContext(
            organization.Id,
            organization.Status,
            organization.Type,
            organization.Enabled,
            UsePolicies: true,
            IsProviderUser: organization.IsProviderUser || organization.IsProviderOrganization)).ToArray();

    private static IReadOnlySet<int> ParseRestrictedTypes(string dataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(dataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return new HashSet<int> { (int)CipherType.Card };

            return document.RootElement
                .EnumerateArray()
                .Where(element => element.TryGetInt32(out _))
                .Select(element => element.GetInt32())
                .ToHashSet();
        }
        catch (JsonException)
        {
            return new HashSet<int> { (int)CipherType.Card };
        }
    }
}
