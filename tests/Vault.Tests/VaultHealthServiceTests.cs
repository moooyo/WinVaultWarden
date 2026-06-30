using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto.PasswordStrength;
using Vault;
using Xunit;

namespace Vault.Tests;

public class VaultHealthServiceTests
{
    private sealed class FakeVault : IVaultService
    {
        private readonly IReadOnlyList<Cipher> _c;
        public FakeVault(IReadOnlyList<Cipher> c) => _c = c;
        public Core.Session.IVaultSnapshot Snapshot => throw new NotSupportedException();
        public IReadOnlyList<Cipher> GetCiphers() => _c;
        public IReadOnlyList<Folder> GetFolders() => Array.Empty<Folder>();
        public IReadOnlyList<DeviceInfo> GetDevices() => Array.Empty<DeviceInfo>();
    }
    private sealed class FakePwned : IPwnedPasswordsClient
    { public Task<int> GetBreachCountAsync(string p, CancellationToken ct = default) => Task.FromResult(0); }

    private static Cipher Login(string id, string name, string? pw, string? uri = null, bool deleted = false) => new()
    {
        Id = id, Type = CipherType.Login, Name = name,
        DeletedDate = deleted ? DateTimeOffset.UtcNow : null,
        Login = new CipherLogin("u", pw, null, uri is null ? Array.Empty<CipherLoginUri>() : new[] { new CipherLoginUri(uri, null) }),
    };

    private static VaultHealthService New(params Cipher[] ciphers) =>
        new(new FakeVault(ciphers),
            new PasswordStrengthEvaluator(new Omnimatch(new DictionaryMatcher(FrequencyDictionaries.Load()))),
            new FakePwned());

    [Fact]
    public void Reused_GroupsByPassword()
    {
        var r = New(Login("1","A","samePass123!"), Login("2","B","samePass123!"), Login("3","C","unique-9xQ2")).AnalyzeOffline();
        var g = Assert.Single(r.Reused);
        Assert.Equal(2, g.Count);
    }

    [Fact]
    public void Weak_FlagsLowScore()
    {
        var r = New(Login("1","A","123456")).AnalyzeOffline();
        Assert.Contains(r.Weak, w => w.Item.CipherId == "1");
    }

    [Fact]
    public void Unsecured_FlagsHttpUri()
    {
        var r = New(Login("1","A","x","http://example.com"), Login("2","B","y","https://secure.com")).AnalyzeOffline();
        var u = Assert.Single(r.Unsecured);
        Assert.Equal("1", u.Item.CipherId);
    }

    [Fact]
    public void IgnoresDeletedAndNonLogin()
    {
        var r = New(Login("1","A","123456", deleted: true)).AnalyzeOffline();
        Assert.Empty(r.Weak);
        Assert.Empty(r.Reused);
    }
}
