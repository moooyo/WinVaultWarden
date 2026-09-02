using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Models;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class VaultCapabilityResolverTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", false)]
    [InlineData("\"true\"", false)]
    [InlineData("1", false)]
    public void Resolve_NewCipherTypes_RequiresExactBooleanTrue(string jsonValue, bool expected)
    {
        var config = Config((VaultCapabilityResolver.NewCipherTypesFeatureKey, jsonValue));

        var capabilities = VaultCapabilityResolver.Resolve(
            config,
            Profile(),
            ServerEndpoints.ForSelfHosted("https://vault.example"));

        Assert.Equal(expected, capabilities.CanWriteNewCipherTypes);
    }

    [Fact]
    public void Resolve_NewCipherTypes_IsCaseSensitiveAndIgnoresServerVersion()
    {
        var config = new ConfigResponse("9999.0.0", null, null)
        {
            FeatureStates = new Dictionary<string, JsonElement>
            {
                [VaultCapabilityResolver.NewCipherTypesFeatureKey.ToUpperInvariant()] = Element("true"),
            },
        };

        var capabilities = VaultCapabilityResolver.Resolve(
            config,
            Profile(),
            ServerEndpoints.ForSelfHosted("https://vault.example"));

        Assert.False(capabilities.CanWriteNewCipherTypes);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", false)]
    [InlineData("\"true\"", false)]
    [InlineData("1", false)]
    public void Resolve_TdeV2JitRequiresExactFeatureBoolean(
        string jsonValue,
        bool expected)
    {
        var config = Config(
            (VaultCapabilityResolver.TdeV2JitProvisioningFeatureKey, jsonValue));

        var capabilities = VaultCapabilityResolver.Resolve(
            config,
            Profile(),
            ServerEndpoints.BitwardenUs);

        Assert.Equal(expected, capabilities.SupportsTdeV2JitProvisioning);
        Assert.Equal(
            expected,
            VaultCapabilityResolver.IsTdeV2JitProvisioningEnabled(config));
    }

    [Fact]
    public void Resolve_TdeV2JitDoesNotInferSupportFromCloudOrServerVersion()
    {
        var capabilities = VaultCapabilityResolver.Resolve(
            new ConfigResponse("9999.0.0", null, null),
            Profile(),
            ServerEndpoints.BitwardenUs);

        Assert.False(capabilities.SupportsTdeV2JitProvisioning);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Resolve_ArchiveEntitlement_AcceptsEitherPremiumSource(
        bool premium,
        bool premiumFromOrganization)
    {
        var capabilities = VaultCapabilityResolver.Resolve(
            new ConfigResponse(null, null, null),
            Profile(premium, premiumFromOrganization),
            ServerEndpoints.BitwardenUs);

        Assert.True(capabilities.SupportsArchiveProtocol);
        Assert.True(capabilities.HasArchiveEntitlement);
        Assert.True(capabilities.CanArchive);
        Assert.True(capabilities.CanUnarchive);
    }

    [Fact]
    public void Resolve_KnownOfficialCloudSupportsArchiveWithoutLegacyFlag()
    {
        var us = VaultCapabilityResolver.Resolve(
            new ConfigResponse(null, null, null),
            Profile(),
            ServerEndpoints.BitwardenUs);
        var eu = VaultCapabilityResolver.Resolve(
            new ConfigResponse(null, null, null),
            Profile(),
            ServerEndpoints.BitwardenEu);

        Assert.True(us.SupportsArchiveProtocol);
        Assert.True(eu.SupportsArchiveProtocol);
        Assert.False(us.CanArchive);
        Assert.True(us.CanUnarchive);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("2026.6.9", null, false)]
    [InlineData("2026.7.0", null, true)]
    [InlineData("2026.7.1", null, true)]
    [InlineData("2027.0.0", null, true)]
    [InlineData("9999.0.0", null, true)]
    [InlineData(null, true, true)]
    [InlineData("2026.6.9", true, true)]
    [InlineData("2026.6.9", false, false)]
    [InlineData("2026.7.0", false, true)]
    public void Resolve_SelfHostedArchiveUsesLegacyFlagOrMinimumServerVersion(
        string? serverVersion,
        bool? legacyFlag,
        bool expected)
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        var config = legacyFlag.HasValue
            ? ConfigWithVersion(
                serverVersion,
                (VaultCapabilityResolver.LegacyArchiveFeatureKey,
                    legacyFlag.Value ? "true" : "false"))
            : ConfigWithVersion(serverVersion);

        var capabilities = VaultCapabilityResolver.Resolve(
            config,
            Profile(premium: true),
            endpoints);

        Assert.Equal(expected, capabilities.SupportsArchiveProtocol);
        Assert.Equal(expected, capabilities.CanArchive);
        Assert.Equal(expected, capabilities.CanUnarchive);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    public void Resolve_SelfHostedArchiveLegacyFlagRequiresExactBooleanTrue(
        string jsonValue)
    {
        var capabilities = VaultCapabilityResolver.Resolve(
            ConfigWithVersion(
                "2026.6.9",
                (VaultCapabilityResolver.LegacyArchiveFeatureKey, jsonValue)),
            Profile(premium: true),
            ServerEndpoints.ForSelfHosted("https://vault.example"));

        Assert.False(capabilities.SupportsArchiveProtocol);
        Assert.True(capabilities.HasArchiveEntitlement);
        Assert.False(capabilities.CanArchive);
        Assert.False(capabilities.CanUnarchive);
    }

    [Theory]
    [InlineData("2026")]
    [InlineData("2026.7")]
    [InlineData("2026.7.0.0")]
    [InlineData("v2026.7.0")]
    [InlineData("2026.7.0-preview")]
    [InlineData("2026.07.0")]
    [InlineData(" 2026.7.0")]
    [InlineData("2026.7.0 ")]
    public void Resolve_SelfHostedArchiveRejectsNonCanonicalServerVersions(
        string serverVersion)
    {
        var capabilities = VaultCapabilityResolver.Resolve(
            ConfigWithVersion(serverVersion),
            Profile(premium: true),
            ServerEndpoints.ForSelfHosted("https://vault.example"));

        Assert.False(capabilities.SupportsArchiveProtocol);
        Assert.False(capabilities.CanArchive);
        Assert.False(capabilities.CanUnarchive);
    }

    [Fact]
    public void Resolve_MissingConfigReturnsAllFalseEvenForOfficialPremiumAccount()
    {
        var capabilities = VaultCapabilityResolver.Resolve(
            null,
            Profile(premium: true, premiumFromOrganization: true),
            ServerEndpoints.BitwardenUs);

        Assert.Equal(VaultCapabilities.None, capabilities);
    }

    [Fact]
    public void CurrentClientVersionMeetsNewCipherTypeMinimum()
    {
        Assert.True(Version.TryParse(ClientHeadersHandler.ClientVersion, out var current));
        Assert.True(current.CompareTo(VaultCapabilityResolver.NewCipherTypesMinimumClientVersion) >= 0);
    }

    private static ConfigResponse Config(params (string Key, string JsonValue)[] values) =>
        ConfigWithVersion(null, values);

    private static ConfigResponse ConfigWithVersion(
        string? version,
        params (string Key, string JsonValue)[] values) =>
        new(version, null, null)
        {
            FeatureStates = values.ToDictionary(
                value => value.Key,
                value => Element(value.JsonValue),
                StringComparer.Ordinal),
        };

    private static JsonElement Element(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static ProfileDto Profile(
        bool premium = false,
        bool premiumFromOrganization = false) =>
        new("account", "user@example.com", "User", null, null)
        {
            Premium = premium,
            PremiumFromOrganization = premiumFromOrganization,
        };
}
