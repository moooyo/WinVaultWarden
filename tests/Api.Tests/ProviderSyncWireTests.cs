using System.Text.Json;
using Api;
using Api.Dtos;
using Xunit;

namespace Api.Tests;

public sealed class ProviderSyncWireTests
{
    [Fact]
    public void SourceGeneratedSyncModelReadsProviderKeyGraphAndPreservesPremiumFields()
    {
        const string json = """
        {
          "object": "sync",
          "profile": {
            "id": "user-1",
            "email": "provider@example.com",
            "name": "Provider User",
            "premium": true,
            "premiumFromOrganization": false,
            "providers": [
              { "id": "provider-1", "key": "4.provider-key" }
            ],
            "providerOrganizations": [
              {
                "id": "org-provider",
                "providerId": "provider-1",
                "key": "2.provider-org-key",
                "name": "Provider Org",
                "status": 2,
                "type": 1,
                "enabled": true
              }
            ],
            "organizations": [
              {
                "id": "org-direct",
                "name": "Direct Org",
                "key": "4.direct-org-key",
                "status": 2,
                "type": 0,
                "enabled": true,
                "providerId": "provider-1"
              }
            ]
          }
        }
        """;

        var sync = JsonSerializer.Deserialize(json, ApiJsonContext.Default.SyncResponse)!;

        Assert.True(sync.Profile!.Premium);
        Assert.False(sync.Profile.PremiumFromOrganization);
        var provider = Assert.Single(sync.Profile.Providers!);
        Assert.Equal("provider-1", provider.Id);
        Assert.Equal("4.provider-key", provider.Key);
        var providerOrganization = Assert.Single(sync.Profile.ProviderOrganizations!);
        Assert.Equal("org-provider", providerOrganization.Id);
        Assert.Equal("provider-1", providerOrganization.ProviderId);
        Assert.Equal("2.provider-org-key", providerOrganization.Key);
        Assert.Equal("Provider Org", providerOrganization.Name);
        Assert.Equal(2, providerOrganization.Status);
        Assert.Equal(1, providerOrganization.Type);
        Assert.True(providerOrganization.Enabled);
        Assert.Equal("provider-1", Assert.Single(sync.Profile.Organizations!).ProviderId);
    }

    [Fact]
    public void ProviderDtosUseOfficialCamelCaseWireNames()
    {
        var providerJson = JsonSerializer.Serialize(
            new ProfileProviderDto("provider-1", "4.key"),
            ApiJsonContext.Default.ProfileProviderDto);
        var organizationJson = JsonSerializer.Serialize(
            new ProfileProviderOrganizationDto(
                "org-1",
                "provider-1",
                "2.key",
                "Provider Org",
                2,
                1,
                true),
            ApiJsonContext.Default.ProfileProviderOrganizationDto);

        Assert.Equal("{\"id\":\"provider-1\",\"key\":\"4.key\"}", providerJson);
        Assert.Equal(
            "{\"id\":\"org-1\",\"providerId\":\"provider-1\",\"key\":\"2.key\",\"name\":\"Provider Org\",\"status\":2,\"type\":1,\"enabled\":true}",
            organizationJson);
    }

    [Fact]
    public void SyncResponseReadsSendsDomainsAndNewPolicyMemberships()
    {
        const string json = """
        {
          "Object": "sync",
          "Profile": {
            "Id": "user-1",
            "Organizations": [],
            "OrganizationsNew": [
              {
                "Id": "org-accepted",
                "Status": 1,
                "Type": 2,
                "Enabled": true,
                "UsePolicies": true
              }
            ]
          },
          "Sends": [],
          "Domains": {
            "EquivalentDomains": [["example.com", "example.net"]],
            "GlobalEquivalentDomains": [
              { "Type": 3, "Domains": ["google.com", "gmail.com"], "Excluded": false }
            ]
          },
          "Policies": [],
          "PoliciesNew": [
            {
              "Id": "policy-1",
              "OrganizationId": "org-accepted",
              "Type": 14,
              "Enabled": true,
              "Data": null,
              "CanToggleState": false
            }
          ]
        }
        """;

        var sync = JsonSerializer.Deserialize(json, ApiJsonContext.Default.SyncResponse)!;

        Assert.Empty(sync.Sends!);
        var organization = Assert.Single(sync.Profile!.OrganizationsNew!);
        Assert.Equal(1, organization.Status);
        Assert.True(organization.UsePolicies);
        Assert.Equal("example.net", sync.Domains!.EquivalentDomains![0][1]);
        Assert.Equal(3, Assert.Single(sync.Domains.GlobalEquivalentDomains!).Type);
        var policy = Assert.Single(sync.PoliciesNew!);
        Assert.Equal(14, policy.Type);
        Assert.False(policy.CanToggleState);
    }

    [Fact]
    public void SyncResponseReadsAccountClaimAndOrganizationProductTierMetadata()
    {
        const string json = """
        {
          "object": "sync",
          "profile": {
            "id": "user-1",
            "organizations": [
              {
                "id": "org-1",
                "status": 2,
                "type": 0,
                "productTierType": 3,
                "userIsClaimedByOrganization": true
              },
              {
                "id": "org-legacy",
                "status": 2,
                "type": 2
              }
            ]
          }
        }
        """;

        var sync = JsonSerializer.Deserialize(json, ApiJsonContext.Default.SyncResponse)!;

        Assert.True(sync.Profile!.Organizations![0].UserIsClaimedByOrganization);
        Assert.Equal(3, sync.Profile.Organizations[0].ProductTierType);
        Assert.Null(sync.Profile.Organizations[1].ProductTierType);
        Assert.Null(sync.Profile.Organizations[1].UserIsClaimedByOrganization);
    }

    [Fact]
    public void SyncResponseMissingAccountClaimMetadataRemainsUnknown()
    {
        const string json = """
        {"object":"sync","profile":{"id":"user-1","organizations":[
          {"id":"org-1","status":2,"type":0}
        ]}}
        """;

        var sync = JsonSerializer.Deserialize(json, ApiJsonContext.Default.SyncResponse)!;

        Assert.Null(Assert.Single(sync.Profile!.Organizations!).UserIsClaimedByOrganization);
    }
}
