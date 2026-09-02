using System.Linq;
using Core.Models;
using Crypto;
using Crypto.PasswordStrength;
using Vault;
using Xunit;

namespace Vault.Tests;

public class VaultHealthServiceDemoExposedTests
{
    private sealed class ThrowingPwned : IPwnedPasswordsClient
    {
        public Task<int> GetBreachCountAsync(string password, CancellationToken ct = default)
            => throw new System.InvalidOperationException("network must not run in demo");
    }

    [Fact]
    public async Task CheckExposedAsync_InDemo_ReturnsCannedFindings_WithoutNetwork()
    {
        var session = new VaultSession();
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(1, 64).Select(i => (byte)i).ToArray()));
        session.SetSnapshot(new DecryptedVault(AccountInfo.Empty, System.Array.Empty<Folder>(), new[]
        {
            new Cipher
            {
                Id = "a", Type = Core.Enums.CipherType.Login, Name = "LinkedIn",
                Login = new CipherLogin("u", "linkedin123", null, System.Array.Empty<CipherLoginUri>()),
            },
            new Cipher
            {
                Id = "b", Type = Core.Enums.CipherType.Login, Name = "Strong",
                Login = new CipherLogin("u", "Qx7$mZ2!vKp9", null, System.Array.Empty<CipherLoginUri>()),
            },
        }, SkippedCipherCount: 0, SkippedFolderCount: 0));
        session.MarkDemo();
        var eval = new PasswordStrengthEvaluator(new Omnimatch(new DictionaryMatcher(FrequencyDictionaries.Load())));
        var svc = new VaultHealthService(new VaultService(session), eval, new ThrowingPwned(), session);

        var findings = await svc.CheckExposedAsync();

        var f = Assert.Single(findings);
        Assert.Equal("LinkedIn", f.Item.Name);
        Assert.True(f.BreachCount > 0);
    }

    [Fact]
    public async Task CheckExposureAsync_InDemo_ReturnsSucceededStatus_WithoutNetwork()
    {
        var session = new VaultSession();
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(1, 64).Select(i => (byte)i).ToArray()));
        session.SetSnapshot(new DecryptedVault(AccountInfo.Empty, System.Array.Empty<Folder>(), new[]
        {
            new Cipher
            {
                Id = "a", Type = Core.Enums.CipherType.Login, Name = "LinkedIn",
                Login = new CipherLogin("u", "linkedin123", null, System.Array.Empty<CipherLoginUri>()),
            },
            new Cipher
            {
                Id = "b", Type = Core.Enums.CipherType.Login, Name = "Strong",
                Login = new CipherLogin("u", "Qx7$mZ2!vKp9", null, System.Array.Empty<CipherLoginUri>()),
            },
        }, SkippedCipherCount: 0, SkippedFolderCount: 0));
        session.MarkDemo();
        var eval = new PasswordStrengthEvaluator(new Omnimatch(new DictionaryMatcher(FrequencyDictionaries.Load())));
        var svc = new VaultHealthService(new VaultService(session), eval, new ThrowingPwned(), session);

        var result = await svc.CheckExposureAsync();

        Assert.Equal(BreachCheckStatus.Succeeded, result.Status);
        Assert.Equal(0, result.FailedQueries);
        Assert.Equal(2, result.SuccessfulQueries); // 两个唯一密码,离线判定视为均已"成功"查询
        Assert.Single(result.Findings);
    }

    [Fact]
    public async Task CheckExposureAsync_InDemo_HiddenPasswordsAreNotReportedOrCounted()
    {
        var session = new VaultSession();
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(1, 64).Select(i => (byte)i).ToArray()));
        session.SetSnapshot(new DecryptedVault(AccountInfo.Empty, System.Array.Empty<Folder>(), new[]
        {
            new Cipher
            {
                Id = "visible", Type = Core.Enums.CipherType.Login, Name = "Visible",
                Login = new CipherLogin("u", "Qx7$mZ2!vKp9", null, System.Array.Empty<CipherLoginUri>()),
            },
            new Cipher
            {
                Id = "hidden", Type = Core.Enums.CipherType.Login, Name = "Hidden",
                CanViewPassword = false,
                Login = new CipherLogin("u", "password", null, System.Array.Empty<CipherLoginUri>()),
            },
        }, SkippedCipherCount: 0, SkippedFolderCount: 0));
        session.MarkDemo();
        var eval = new PasswordStrengthEvaluator(new Omnimatch(new DictionaryMatcher(FrequencyDictionaries.Load())));
        var service = new VaultHealthService(new VaultService(session), eval, new ThrowingPwned(), session);

        var result = await service.CheckExposureAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BreachCheckStatus.Succeeded, result.Status);
        Assert.Equal(1, result.SuccessfulQueries);
        Assert.Equal(0, result.FailedQueries);
        Assert.Empty(result.Findings);
    }
}
