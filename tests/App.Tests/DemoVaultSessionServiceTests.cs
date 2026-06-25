using App.Services;
using Core.Enums;
using Core.Passkeys;
using Core.Session;
using Vault;
using Xunit;

namespace App.Tests;

public class DemoVaultSessionServiceTests
{
    [Fact]
    public async Task OpenDemoVaultAsync_PopulatesUnlockedSnapshotWithoutTokens()
    {
        var session = new VaultSession();
        var service = new DemoVaultSessionService(session);

        await service.OpenDemoVaultAsync(CancellationToken.None);

        Assert.Equal(VaultState.Unlocked, session.State);
        Assert.Equal("demo@winvaultwarden.local", session.Account.Email);
        Assert.Null(session.AccessToken);
        Assert.Null(session.RefreshToken);
        Assert.NotNull(session.UserKey);
        Assert.Contains(session.Ciphers, c => c.Type == CipherType.Login);
        Assert.Contains(session.Ciphers, c => c.Type == CipherType.Card);
        Assert.Contains(session.Ciphers, c => c.Type == CipherType.Identity);
        Assert.Contains(session.Ciphers, c => c.Type == CipherType.SecureNote);
        Assert.Contains(session.Ciphers, c => c.Type == CipherType.SshKey);
        Assert.Contains(session.Ciphers, c => c.DeletedDate is not null);
        var localPasskey = Assert.Single(session.Ciphers, c => c.Id == "demo-local-passkey");
        var credential = Assert.Single(localPasskey.Login!.Fido2Credentials);
        Assert.Equal("localhost", credential.RpId);
        Assert.False(string.IsNullOrWhiteSpace(WebAuthnAssertionService.CreateAssertion(
            credential,
            new WebAuthnGetAssertionRequest("http://localhost:8787", "AQIDBA", "localhost", [], false)).Signature));
        Assert.Single(session.Folders);
        Assert.Single(session.Devices);
    }
}
