using Core.Session;

namespace Core.Models;

public enum SendMutationKind
{
    Create,
    Update,
    Replace,
    Delete,
}

/// <summary>
/// Identifies a confirmed Send mutation and the server revision returned by that mutation.
/// Revision may be absent on older compatible servers; same-ID updates then remain pending.
/// </summary>
public sealed record SendMutationReceipt(
    string SendId,
    DateTimeOffset? RevisionDate,
    SendMutationKind Kind,
    string? AccessId = null,
    string? RawMutationWitness = null,
    VaultSessionLease? SessionLease = null,
    string? ReplacedSendId = null);
