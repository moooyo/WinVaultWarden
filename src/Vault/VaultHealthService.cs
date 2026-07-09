using Core.Models;
using Core.Services;
using Crypto.PasswordStrength;

namespace Vault;

public sealed class VaultHealthService : IVaultHealthService
{
    // 演示会话(VaultSession.IsDemo)已知泄露密码 → 命中次数,供 CheckExposedAsync 离线返回,
    // 不触网(演示会话未配置服务端地址,任何 HIBP 请求都会抛「An invalid request URI was provided…」)。
    private static readonly Dictionary<string, int> DemoBreachCounts = new(StringComparer.Ordinal)
    {
        ["123456"] = 37359195, ["password"] = 9659365, ["qwerty"] = 3912816,
        ["admin"] = 56789, ["Summer2024"] = 872, ["linkedin123"] = 1502, ["adobe123"] = 4211,
    };

    private readonly IVaultService _vault;
    private readonly PasswordStrengthEvaluator _evaluator;
    private readonly IPwnedPasswordsClient _pwned;
    private readonly VaultSession? _session;

    public VaultHealthService(IVaultService vault, PasswordStrengthEvaluator evaluator, IPwnedPasswordsClient pwned, VaultSession? session = null)
    { _vault = vault; _evaluator = evaluator; _pwned = pwned; _session = session; }

    private IEnumerable<Cipher> ActiveLogins() =>
        _vault.GetCiphers().Where(c => !c.IsDeleted && c.Login is not null);

    private static HealthItemRef Ref(Cipher c) => new(c.Id, c.Name, c.Login?.Username);

    public HealthReport AnalyzeOffline()
    {
        var logins = ActiveLogins().ToList();

        var reused = logins
            .Where(c => !string.IsNullOrEmpty(c.Login!.Password))
            .GroupBy(c => c.Login!.Password!, StringComparer.Ordinal)
            .Where(g => g.Count() >= 2)
            .Select(g => new ReusedGroup(g.Count(), g.Select(Ref).ToArray()))
            .ToArray();

        var weak = logins
            .Where(c => !string.IsNullOrEmpty(c.Login!.Password))
            .Select(c => (c, score: _evaluator.Evaluate(c.Login!.Password!).Score))
            .Where(x => x.score <= 2)
            .Select(x => new WeakFinding(Ref(x.c), x.score))
            .ToArray();

        var unsecured = logins
            .SelectMany(c => (c.Login!.Uris ?? Array.Empty<CipherLoginUri>())
                .Where(u => u.Uri is not null && u.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                .Select(u => new UnsecuredFinding(Ref(c), u.Uri!)))
            .ToArray();

        return new HealthReport(reused, weak, unsecured);
    }

    // Task 7
    public async Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default)
    {
        // 演示/离线会话:不发起任何 HIBP 请求,直接从内置已知泄露密码表离线判定(Task 3)。
        if (_session?.IsDemo == true)
        {
            return ActiveLogins()
                .Where(c => !string.IsNullOrEmpty(c.Login!.Password) && DemoBreachCounts.ContainsKey(c.Login!.Password!))
                .Select(c => new ExposedFinding(Ref(c), DemoBreachCounts[c.Login!.Password!]))
                .ToArray();
        }

        var logins = ActiveLogins().Where(c => !string.IsNullOrEmpty(c.Login!.Password)).ToList();
        var unique = logins.Select(c => c.Login!.Password!).Distinct(StringComparer.Ordinal).ToList();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pw in unique)
        {
            try { counts[pw] = await _pwned.GetBreachCountAsync(pw, ct); }
            catch (OperationCanceledException) { throw; }
            catch { counts[pw] = 0; } // 单项失败降级为 0，不阻塞整体
        }

        return logins
            .Where(c => counts.GetValueOrDefault(c.Login!.Password!) > 0)
            .Select(c => new ExposedFinding(Ref(c), counts[c.Login!.Password!]))
            .ToArray();
    }
}
