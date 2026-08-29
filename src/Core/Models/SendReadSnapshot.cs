using Core.Session;

namespace Core.Models;

public enum SendDecryptStatus
{
    Decrypted,
    Corrupt,
}

/// <summary>
/// Preserves the server identity and revision of every raw Send, including entries that cannot be
/// decrypted. UI projection may hide corrupt values, but reconciliation must still observe them.
/// </summary>
public sealed record SendObservation(
    string Id,
    DateTimeOffset? RevisionDate,
    SendDecryptStatus DecryptStatus,
    Send? Value,
    string? RawMutationWitness = null);

public sealed record SendReadSnapshot(
    IReadOnlyList<SendObservation> Observations,
    VaultSessionLease? SessionLease = null,
    bool IsComplete = true);
