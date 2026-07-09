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
        }, 0));
        session.MarkDemo();
        var eval = new PasswordStrengthEvaluator(new Omnimatch(new DictionaryMatcher(FrequencyDictionaries.Load())));
        var svc = new VaultHealthService(new VaultService(session), eval, new ThrowingPwned(), session);

        var findings = await svc.CheckExposedAsync();

        var f = Assert.Single(findings);
        Assert.Equal("LinkedIn", f.Item.Name);
        Assert.True(f.BreachCount > 0);
    }
}
