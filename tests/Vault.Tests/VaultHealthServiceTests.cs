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
        public Core.Session.VaultSessionLease CaptureLease() =>
            throw new InvalidOperationException("This fake has no vault session.");
        public bool IsCurrent(Core.Session.VaultSessionLease lease) => false;
        public void ThrowIfExpired(Core.Session.VaultSessionLease lease) =>
            throw new OperationCanceledException(lease.CancellationToken);
        public IReadOnlyList<Cipher> GetCiphers(Core.Session.VaultSessionLease lease) =>
            throw new OperationCanceledException(lease.CancellationToken);
    }
    private sealed class FakePwned : IPwnedPasswordsClient
    { public Task<int> GetBreachCountAsync(string p, CancellationToken ct = default) => Task.FromResult(0); }

    private static Cipher Login(
        string id,
        string name,
        string? pw,
        string? uri = null,
        bool deleted = false,
        bool canViewPassword = true) => new()
    {
        Id = id, Type = CipherType.Login, Name = name,
        DeletedDate = deleted ? DateTimeOffset.UtcNow : null,
        CanViewPassword = canViewPassword,
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

    [Fact]
    public void AnalyzeOffline_HiddenPasswords_DoNotAffectPasswordFindingsOrCounts()
    {
        var report = New(
            Login("visible", "Visible", "shared-password"),
            Login("hidden-shared", "Hidden shared", "shared-password", canViewPassword: false),
            Login("hidden-weak", "Hidden weak", "123456", canViewPassword: false))
            .AnalyzeOffline();

        Assert.Empty(report.Reused);
        Assert.DoesNotContain(report.Weak, finding =>
            finding.Item.CipherId is "hidden-shared" or "hidden-weak");
    }

    [Fact]
    public void AnalyzeOffline_HiddenPasswordPermission_DoesNotSuppressUriAudit()
    {
        var report = New(Login(
            "hidden",
            "Hidden password",
            "123456",
            "http://example.com",
            canViewPassword: false)).AnalyzeOffline();

        var finding = Assert.Single(report.Unsecured);
        Assert.Equal("hidden", finding.Item.CipherId);
        Assert.Empty(report.Reused);
        Assert.Empty(report.Weak);
    }

    private sealed class MapPwned : IPwnedPasswordsClient
    {
        private readonly Dictionary<string, int> _map;
        public Dictionary<string, int> Calls { get; } = new();
        public MapPwned(Dictionary<string, int> map) => _map = map;
        public Task<int> GetBreachCountAsync(string p, CancellationToken ct = default)
        { Calls[p] = Calls.GetValueOrDefault(p) + 1; return Task.FromResult(_map.GetValueOrDefault(p)); }
    }

    [Fact]
    public async Task CheckExposed_DedupesAndMapsBackToAllItems()
    {
        var pwned = new MapPwned(new() { ["leaked-pw"] = 42, ["clean-pw"] = 0 });
        var svc = new VaultHealthService(
            new FakeVault(new[] { Login("1","A","leaked-pw"), Login("2","B","leaked-pw"), Login("3","C","clean-pw") }),
            new PasswordStrengthEvaluator(new Omnimatch(new DictionaryMatcher(FrequencyDictionaries.Load()))),
            pwned);

        var exposed = await svc.CheckExposedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, exposed.Count);                       // 两条用了泄露密码
        Assert.All(exposed, e => Assert.Equal(42, e.BreachCount));
        Assert.Equal(1, pwned.Calls["leaked-pw"]);            // 去重：每唯一密码只查一次
    }

    [Fact]
    public async Task CheckExposure_HiddenPasswords_AreNotQueriedReportedOrCounted()
    {
        var pwned = new MapPwned(new()
        {
            ["visible-password"] = 42,
            ["hidden-password"] = 99,
        });
        var service = HealthService(
            pwned,
            Login("visible", "Visible", "visible-password"),
            Login("hidden", "Hidden", "hidden-password", canViewPassword: false),
            Login("hidden-duplicate", "Hidden duplicate", "visible-password", canViewPassword: false));

        var result = await service.CheckExposureAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BreachCheckStatus.Succeeded, result.Status);
        Assert.Equal(1, result.SuccessfulQueries);
        Assert.Equal(0, result.FailedQueries);
        Assert.Equal("visible", Assert.Single(result.Findings).Item.CipherId);
        Assert.Equal(1, pwned.Calls["visible-password"]);
        Assert.DoesNotContain("hidden-password", pwned.Calls.Keys);
    }

    // ── Task 14: 显式 BreachCheckResult 状态 ─────────────────────────────────

    private sealed class ScriptedPwnedClient(
        IReadOnlyDictionary<string, object> results) : IPwnedPasswordsClient
    {
        public Task<int> GetBreachCountAsync(
            string password,
            CancellationToken ct = default)
        {
            var result = results[password];
            return result is Exception exception
                ? Task.FromException<int>(exception)
                : Task.FromResult((int)result);
        }
    }

    private sealed class CancelingPwnedClient(CancellationTokenSource cts) : IPwnedPasswordsClient
    {
        public Task<int> GetBreachCountAsync(string password, CancellationToken ct = default)
        {
            // 模拟调用方在请求进行中主动取消：ct 与调用方 cts 是同一个 token,
            // 抛出的 OperationCanceledException.CancellationToken 应等于该 token。
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }
    }

    private static VaultHealthService HealthService(
        IPwnedPasswordsClient client,
        params Cipher[] ciphers) =>
        new(
            new FakeVault(ciphers),
            new PasswordStrengthEvaluator(
                new Omnimatch(new DictionaryMatcher(FrequencyDictionaries.Load()))),
            client);

    [Fact]
    public async Task CheckExposure_OnePrefixFails_ReturnsPartialAndKeepsKnownFindings()
    {
        var client = new ScriptedPwnedClient(new Dictionary<string, object>
        {
            ["leaked"] = 77,
            ["unknown"] = new HttpRequestException("service unavailable"),
        });
        var service = HealthService(client,
            Login("1", "Known", "leaked"),
            Login("2", "Unknown", "unknown"));

        var result = await service.CheckExposureAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BreachCheckStatus.PartialFailure, result.Status);
        Assert.Equal(1, result.SuccessfulQueries);
        Assert.Equal(1, result.FailedQueries);
        Assert.Equal(77, Assert.Single(result.Findings).BreachCount);
    }

    [Fact]
    public async Task CheckExposure_AllTimeout_ReturnsFailedNotZero()
    {
        var client = new ScriptedPwnedClient(new Dictionary<string, object>
        {
            ["a"] = new TaskCanceledException("timeout"),
            ["b"] = new TaskCanceledException("timeout"),
        });
        var service = HealthService(
            client,
            Login("1", "A", "a"),
            Login("2", "B", "b"));

        var result = await service.CheckExposureAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BreachCheckStatus.Failed, result.Status);
        Assert.Equal(0, result.SuccessfulQueries);
        Assert.Equal(2, result.FailedQueries);
        await Assert.ThrowsAsync<BreachCheckIncompleteException>(() =>
            service.CheckExposedAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CheckExposure_CallerCancels_PropagatesOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var client = new CancelingPwnedClient(cts);
        var service = HealthService(client, Login("1", "A", "secret"));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.CheckExposureAsync(cts.Token));
    }

    [Fact]
    public void AnalyzeOffline_ExposureStatusStartsUnknown()
    {
        var report = HealthService(
            new ScriptedPwnedClient(new Dictionary<string, object>()),
            Login("1", "A", "password"))
            .AnalyzeOffline();

        Assert.Equal(BreachCheckStatus.Unknown, report.Exposure.Status);
    }
}
