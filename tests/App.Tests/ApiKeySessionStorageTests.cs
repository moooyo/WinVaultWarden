using System.Text.Json;
using System.Text.Json.Nodes;
using App.Services;
using Core.Enums;
using Core.Models;
using Xunit;

namespace App.Tests;

public sealed class ApiKeySessionStorageTests
{
    [Fact]
    public void ApiKeyOfflineSessionRoundTripsThroughTheActualSourceGeneratedStorageContext()
    {
        var session = ApiKeySession();

        var json = JsonSerializer.SerializeToUtf8Bytes(session, AppJsonContext.Default.PersistedSession);
        var restored = JsonSerializer.Deserialize(json, AppJsonContext.Default.PersistedSession);

        Assert.NotNull(restored);
        Assert.True(restored.HasValidBinding);
        Assert.True(restored.IsApiKeySession);
        Assert.Empty(restored.RefreshToken);
        Assert.Equal(session.AccountId, restored.AccountId);
        Assert.Equal(session.Endpoints, restored.Endpoints);
        Assert.Equal(session.ProtectedUserKey, restored.ProtectedUserKey);
        Assert.Equal(session.MasterPasswordSalt, restored.MasterPasswordSalt);
        Assert.Equal(session, restored);
    }

    [Fact]
    public void MissingApiModeFlagCannotTurnAnInvalidEmptyRefreshSessionIntoAnApiKeySession()
    {
        var json = JsonSerializer.Serialize(ApiKeySession(), AppJsonContext.Default.PersistedSession);
        var node = JsonNode.Parse(json)!.AsObject();
        Assert.True(node.Remove("isApiKeySession"));

        var restored = JsonSerializer.Deserialize(node.ToJsonString(), AppJsonContext.Default.PersistedSession);

        Assert.NotNull(restored);
        Assert.False(restored.IsApiKeySession);
        Assert.False(restored.HasValidBinding);
        Assert.False(restored.TryMigrate(out _));
    }

    [Fact]
    public void OrdinarySessionsStillRequireAnActualRefreshToken()
    {
        var session = ApiKeySession() with { IsApiKeySession = false };

        Assert.False(session.HasValidBinding);
        Assert.False(session.TryMigrate(out _));
        Assert.True((session with { RefreshToken = "synthetic-refresh-token" }).HasValidBinding);
    }

    [Theory]
    [InlineData("synthetic-refresh-token")]
    [InlineData(" ")]
    [InlineData(null)]
    public void ApiKeyModeDoesNotAcceptNonemptyOrAmbiguousRefreshState(string? refreshToken)
    {
        var session = ApiKeySession() with { RefreshToken = refreshToken! };

        Assert.False(session.HasValidBinding);
        Assert.False(session.TryMigrate(out _));
    }

    [Theory]
    [InlineData(PersistedSessionUnlockMethod.KeyConnector)]
    [InlineData(PersistedSessionUnlockMethod.TrustedDeviceSso)]
    public void ApiKeyOfflineStateCannotBeReinterpretedAsAnotherUnlockProtocol(PersistedSessionUnlockMethod method)
    {
        var session = ApiKeySession() with
        {
            UnlockMethod = method,
            KeyConnectorUrl = method == PersistedSessionUnlockMethod.KeyConnector ? "https://keys.example" : null,
        };

        Assert.False(session.HasValidBinding);
        Assert.False(session.TryMigrate(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void ApiKeyOfflineStateRequiresAnExplicitMasterPasswordUnlockCapability(bool? hasMasterPassword)
    {
        var session = ApiKeySession() with { HasMasterPassword = hasMasterPassword };

        Assert.False(session.HasValidBinding);
        Assert.False(session.TryMigrate(out _));
    }

    private static PersistedSession ApiKeySession() => new(
        PersistedSession.CurrentSchemaVersion,
        "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        ServerEndpoints.ForSelfHosted("https://vault.example"),
        1,
        "me@example.com",
        "11111111-2222-3333-4444-555555555555",
        string.Empty,
        "2.synthetic-encrypted-user-key",
        KdfType.Pbkdf2,
        600_000,
        null,
        null)
    {
        IsApiKeySession = true,
        HasMasterPassword = true,
    };
}
