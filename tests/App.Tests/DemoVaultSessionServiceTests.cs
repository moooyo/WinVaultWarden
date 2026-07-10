using System.Linq;
using App.Services;
using Core.Enums;
using Core.Passkeys;
using Core.Services;
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
        Assert.False(string.IsNullOrWhiteSpace(
            WebAuthnAssertionService.CreateAssertion(
                credential,
                new WebAuthnGetAssertionRequest(
                    "http://localhost:8787",
                    "AQIDBA",
                    "localhost",
                    [],
                    new PasskeyApprovalEvidence(
                        Approved: true,
                        UserPresent: true,
                        UserVerified: false)))
            .Signature));
        Assert.True(session.Folders.Count >= 4, $"folders={session.Folders.Count}");
        Assert.Single(session.Devices);

        // 演示会话被标记为离线,并预置演示 Send(文本 + 文件),供 Send 页展示且不触网。
        Assert.True(session.IsDemo);
        Assert.Contains(session.Sends, s => s.Type == Core.Enums.SendType.Text);
        Assert.Contains(session.Sends, s => s.Type == Core.Enums.SendType.File);
        Assert.All(session.Sends, s => Assert.False(string.IsNullOrWhiteSpace(s.ShareUrl)));
    }

    [Fact]
    public async Task OpenDemoVaultAsync_PopulatesRichDataThatLightsUpSecurityReport()
    {
        var session = new VaultSession();
        await new DemoVaultSessionService(session).OpenDemoVaultAsync(CancellationToken.None);
        var logins = session.Ciphers.Where(c => !c.IsDeleted && c.Login is not null).ToList();
        Assert.True(logins.Count >= 10, $"logins={logins.Count}");
        Assert.Contains(logins.GroupBy(c => c.Login!.Password), g => g.Count() >= 2);
        Assert.True(logins.SelectMany(c => c.Login!.Uris).Count(u => u.Uri?.StartsWith("http://") == true) >= 2);
        Assert.True(session.Folders.Count >= 4, $"folders={session.Folders.Count}");
        Assert.True(session.Ciphers.Count(c => c.Type == Core.Enums.CipherType.SecureNote) >= 2);
    }
}
