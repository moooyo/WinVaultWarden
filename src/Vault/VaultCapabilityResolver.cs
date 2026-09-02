using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Models;

namespace Vault;

public static class VaultCapabilityResolver
{
    public const string NewCipherTypesFeatureKey = "pm-32009-new-item-types";
    public const string LegacyArchiveFeatureKey = "pm-19148-innovation-archive";
    public const string TdeV2JitProvisioningFeatureKey =
        "pm-27279-v2-registration-tde-jit";
    public static readonly Version NewCipherTypesMinimumClientVersion = new(2026, 2, 0);
    public static readonly Version ArchiveMinimumServerVersion = new(2026, 7, 0);

    public static VaultCapabilities Resolve(
        ConfigResponse? config,
        ProfileDto? profile,
        ServerEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (config is null)
            return VaultCapabilities.None;

        var canWriteNewCipherTypes =
            IsStrictlyEnabled(config, NewCipherTypesFeatureKey)
            && Version.TryParse(ClientHeadersHandler.ClientVersion, out var clientVersion)
            && clientVersion.CompareTo(NewCipherTypesMinimumClientVersion) >= 0;
        var supportsArchiveProtocol =
            endpoints == ServerEndpoints.BitwardenUs
            || endpoints == ServerEndpoints.BitwardenEu
            || IsStrictlyEnabled(config, LegacyArchiveFeatureKey)
            || IsAtLeastStrictThreePartVersion(
                config.Version,
                ArchiveMinimumServerVersion);
        var hasArchiveEntitlement =
            profile?.Premium is true
            || profile?.PremiumFromOrganization is true;
        var supportsTdeV2JitProvisioning =
            IsTdeV2JitProvisioningEnabled(config);

        return new VaultCapabilities(
            canWriteNewCipherTypes,
            supportsArchiveProtocol,
            hasArchiveEntitlement,
            supportsTdeV2JitProvisioning);
    }

    public static bool IsTdeV2JitProvisioningEnabled(ConfigResponse? config) =>
        config is not null
        && IsStrictlyEnabled(config, TdeV2JitProvisioningFeatureKey);

    private static bool IsStrictlyEnabled(ConfigResponse config, string key) =>
        config.FeatureStates?.TryGetValue(key, out var value) is true
        && value.ValueKind == JsonValueKind.True;

    private static bool IsAtLeastStrictThreePartVersion(
        string? value,
        Version minimum)
    {
        if (!Version.TryParse(value, out var version)
            || version.Build < 0
            || version.Revision >= 0
            || !string.Equals(value, version.ToString(3), StringComparison.Ordinal))
        {
            return false;
        }

        return version.CompareTo(minimum) >= 0;
    }
}
