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

    // Task 14
    public async Task<BreachCheckResult> CheckExposureAsync(CancellationToken ct = default)
    {
        // 演示/离线会话:不发起任何 HIBP 请求,直接从内置已知泄露密码表离线判定(Task 3),
        // 视为全部唯一密码均"成功"查询完成。
        if (_session?.IsDemo == true)
        {
            var demoLogins = ActiveLogins()
                .Where(cipher => !string.IsNullOrEmpty(cipher.Login!.Password))
                .ToArray();
            var demo = demoLogins
                .Where(cipher => DemoBreachCounts.ContainsKey(cipher.Login!.Password!))
                .Select(cipher => new ExposedFinding(
                    Ref(cipher),
                    DemoBreachCounts[cipher.Login!.Password!]))
                .ToArray();
            return new BreachCheckResult(
                BreachCheckStatus.Succeeded,
                demo,
                demoLogins
                    .Select(cipher => cipher.Login!.Password!)
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                0);
        }

        var logins = ActiveLogins().Where(cipher => !string.IsNullOrEmpty(cipher.Login!.Password)).ToList();
        var unique = logins
            .Select(cipher => cipher.Login!.Password!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var failures = 0;

        foreach (var password in unique)
        {
            try
            {
                counts[password] = await _pwned.GetBreachCountAsync(password, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 调用者主动取消:必须继续向上抛出,不得计为失败查询。
                throw;
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                    or TaskCanceledException
                    or InvalidDataException)
            {
                // 网络失败、非调用者触发的超时、损坏的 HIBP 响应:计为失败查询,继续处理其余密码。
                failures++;
            }
        }

        var findings = logins
            .Where(cipher =>
                counts.TryGetValue(cipher.Login!.Password!, out var count) && count > 0)
            .Select(cipher => new ExposedFinding(Ref(cipher), counts[cipher.Login!.Password!]))
            .ToArray();
        var successes = counts.Count;
        var status = failures switch
        {
            0 => BreachCheckStatus.Succeeded,
            _ when successes == 0 => BreachCheckStatus.Failed,
            _ => BreachCheckStatus.PartialFailure,
        };
        return new BreachCheckResult(status, findings, successes, failures);
    }

    // Task 7 / Task 14: 兼容方法,只有 Succeeded 才返回 Findings。
    public async Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default)
    {
        var result = await CheckExposureAsync(ct).ConfigureAwait(false);
        return result.Status == BreachCheckStatus.Succeeded
            ? result.Findings
            : throw new BreachCheckIncompleteException(result);
    }
}
