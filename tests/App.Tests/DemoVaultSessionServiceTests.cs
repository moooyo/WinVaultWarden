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

        // 演示会话被标记为离线,并预置演示 Send(文本 + 文件),供 Send 页展示且不触网。
        Assert.True(session.IsDemo);
        Assert.Contains(session.Sends, s => s.Type == Core.Enums.SendType.Text);
        Assert.Contains(session.Sends, s => s.Type == Core.Enums.SendType.File);
        Assert.All(session.Sends, s => Assert.False(string.IsNullOrWhiteSpace(s.ShareUrl)));
    }
}
