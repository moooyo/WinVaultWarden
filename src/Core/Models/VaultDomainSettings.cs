namespace Core.Models;

public sealed record VaultGlobalEquivalentDomain(
    int Type,
    IReadOnlyList<string> Domains,
    bool Excluded);

/// <summary>
/// Preserves the optional Domains section of a full sync response. A null snapshot means the
/// client requested excludeDomains or the server omitted the section.
/// </summary>
public sealed record VaultDomainSettings(
    IReadOnlyList<IReadOnlyList<string>> EquivalentDomains,
    IReadOnlyList<VaultGlobalEquivalentDomain> GlobalEquivalentDomains);
