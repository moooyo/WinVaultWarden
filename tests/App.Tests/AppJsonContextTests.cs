using System.Text.Json;
using App.Services;
using Core.Enums;
using Core.Models;
using Xunit;

namespace App.Tests;

public class AppJsonContextTests
{
    [Fact]
    public void PersistedSession_roundtrips_via_context()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://srv/warden");
        var s = new PersistedSession(PersistedSession.CurrentSchemaVersion, "a@b.com", endpoints, 1, "a@b.com", "dev", "rt", "2.key", KdfType.Pbkdf2, 600000, null, null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(s, AppJsonContext.Default.PersistedSession);
        var back = JsonSerializer.Deserialize(bytes, AppJsonContext.Default.PersistedSession)!;
        Assert.Equal(s, back);
        Assert.Equal("https://srv/warden/api", back.Endpoints!.Api.Value);
    }

    [Fact]
    public void PersistedSession_V2CryptographicState_roundtrips_via_context()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://srv/warden");
        var state = new PersistedAccountCryptographicState(
            2,
            "7.wrapped-private-key",
            WrappedSigningKey: "7.wrapped-signing-key",
            SignedSecurityState: "signed-security-state");
        var session = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "a@b.com",
            endpoints,
            1,
            "a@b.com",
            "dev",
            "rt",
            "7.protected-user-key",
            KdfType.Pbkdf2,
            600000,
            null,
            null,
            userKeySecurityVersion: 2,
            userKeyId: "00112233445566778899aabbccddeeff",
            accountCryptographicState: state)
        {
            SupportsDeviceTrust = true,
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            session,
            AppJsonContext.Default.PersistedSession);
        var roundtripped = JsonSerializer.Deserialize(
            bytes,
            AppJsonContext.Default.PersistedSession)!;

        Assert.Equal(session, roundtripped);
        Assert.True(roundtripped.HasValidBinding);
        Assert.Equal(2, roundtripped.UserKeySecurityVersion);
        Assert.True(roundtripped.SupportsDeviceTrust);
        Assert.Equal("00112233445566778899aabbccddeeff", roundtripped.UserKeyId);
        Assert.Equal(state, roundtripped.AccountCryptographicState);
        Assert.Null(roundtripped.AccountCryptographicState!.PublicKey);
        Assert.Null(roundtripped.AccountCryptographicState.SignedPublicKey);
        Assert.Null(roundtripped.AccountCryptographicState.VerifyingKey);
    }

    [Fact]
    public void AppPreferencesData_roundtrips_via_context()
    {
        var p = new AppPreferencesData { ThemeIndex = 2 };
        var json = JsonSerializer.Serialize(p, AppJsonContext.Default.AppPreferencesData);
        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppPreferencesData)!;
        Assert.Equal(2, back.ThemeIndex);
    }
}
