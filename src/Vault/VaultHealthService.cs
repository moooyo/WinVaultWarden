using Core.Models;
using Core.Services;
using Crypto.PasswordStrength;

namespace Vault;

public sealed class VaultHealthService : IVaultHealthService
{
    private readonly IVaultService _vault;
    private readonly PasswordStrengthEvaluator _evaluator;
    private readonly IPwnedPasswordsClient _pwned;

    public VaultHealthService(IVaultService vault, PasswordStrengthEvaluator evaluator, IPwnedPasswordsClient pwned)
    { _vault = vault; _evaluator = evaluator; _pwned = pwned; }

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
    public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default)
        => throw new NotImplementedException();
}
