using System.Text.Json;
using Api;
using Xunit;

namespace Api.Tests;

public sealed class ConfigWireTests
{
    [Fact]
    public void ConfigResponse_SourceGeneratedContext_PreservesFeatureValueKinds()
    {
        const string json =
            """
            {
              "version": "2026.8.0",
              "gitHash": "abc123",
              "server": null,
              "featureStates": {
                "bool-true": true,
                "bool-false": false,
                "null-value": null,
                "string-value": "true",
                "number-value": 1,
                "Mixed-Case-Key": true
              }
            }
            """;

        var config = JsonSerializer.Deserialize(json, ApiJsonContext.Default.ConfigResponse)!;

        Assert.Equal(JsonValueKind.True, config.FeatureStates!["bool-true"].ValueKind);
        Assert.Equal(JsonValueKind.False, config.FeatureStates["bool-false"].ValueKind);
        Assert.Equal(JsonValueKind.Null, config.FeatureStates["null-value"].ValueKind);
        Assert.Equal(JsonValueKind.String, config.FeatureStates["string-value"].ValueKind);
        Assert.Equal(JsonValueKind.Number, config.FeatureStates["number-value"].ValueKind);
        Assert.True(config.FeatureStates.ContainsKey("Mixed-Case-Key"));
    }

    [Fact]
    public void ConfigResponse_MissingNullAndEmptyFeatureStatesRemainDistinct()
    {
        var missing = JsonSerializer.Deserialize(
            "{\"version\":null,\"gitHash\":null,\"server\":null}",
            ApiJsonContext.Default.ConfigResponse)!;
        var explicitNull = JsonSerializer.Deserialize(
            "{\"version\":null,\"gitHash\":null,\"server\":null,\"featureStates\":null}",
            ApiJsonContext.Default.ConfigResponse)!;
        var empty = JsonSerializer.Deserialize(
            "{\"version\":null,\"gitHash\":null,\"server\":null,\"featureStates\":{}}",
            ApiJsonContext.Default.ConfigResponse)!;

        Assert.Null(missing.FeatureStates);
        Assert.Null(explicitNull.FeatureStates);
        Assert.Empty(empty.FeatureStates!);
    }

    [Fact]
    public void ServerConfigRole_DoesNotWidenIdentityOrAuthenticatedAggregateMarkers()
    {
        Assert.True(typeof(IAnonymousApiRole).IsAssignableFrom(typeof(IServerConfigApiClient)));
        Assert.True(typeof(IAuthenticatedApiRole).IsAssignableFrom(typeof(IServerConfigApiClient)));
        Assert.False(typeof(IAuthenticatedApiRole).IsAssignableFrom(typeof(IIdentityApiClient)));
        Assert.False(typeof(IAnonymousApiRole).IsAssignableFrom(typeof(IAuthenticatedApiClient)));
    }

    [Fact]
    public void SyncProfile_SourceGeneratedContext_ReadsPremiumSourcesAndDefaultsMissingToNull()
    {
        var populated = JsonSerializer.Deserialize(
            """
            {"object":"sync","profile":{"id":"account","email":"user@example.com","name":"User",
             "premium":true,"premiumFromOrganization":false},"folders":[],"ciphers":[]}
            """,
            ApiJsonContext.Default.SyncResponse)!;
        var missing = JsonSerializer.Deserialize(
            """
            {"object":"sync","profile":{"id":"account","email":"user@example.com","name":"User"},
             "folders":[],"ciphers":[]}
            """,
            ApiJsonContext.Default.SyncResponse)!;

        Assert.True(populated.Profile!.Premium);
        Assert.False(populated.Profile.PremiumFromOrganization);
        Assert.Null(missing.Profile!.Premium);
        Assert.Null(missing.Profile.PremiumFromOrganization);
    }
}
