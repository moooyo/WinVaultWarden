using Core.Enums;
using System.Security.Cryptography;
using System.Text;

namespace Core.Models;

public enum CipherCreateReconciliationStatus
{
    Confirmed,
    NotFound,
    Ambiguous,
    SnapshotMismatch,
    SessionMismatch,
}

public sealed record CipherCreateWitness
{
    public CipherCreateWitness(
        string accountId,
        ServerEndpoints endpoints,
        CipherType cipherType,
        string? organizationId,
        IReadOnlyList<string> collectionIds,
        IReadOnlyList<string> baselineCipherIds,
        string keyFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(collectionIds);
        ArgumentNullException.ThrowIfNull(baselineCipherIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFingerprint);

        AccountId = accountId;
        Endpoints = endpoints;
        CipherType = cipherType;
        OrganizationId = string.IsNullOrWhiteSpace(organizationId) ? null : organizationId;
        CollectionIds = FreezeDistinct(collectionIds, nameof(collectionIds));
        BaselineCipherIds = FreezeDistinct(baselineCipherIds, nameof(baselineCipherIds));
        KeyFingerprint = keyFingerprint.ToUpperInvariant();
    }

    public string AccountId { get; }
    public ServerEndpoints Endpoints { get; }
    public CipherType CipherType { get; }
    public string? OrganizationId { get; }
    public IReadOnlyList<string> CollectionIds { get; }
    public IReadOnlyList<string> BaselineCipherIds { get; }
    public string KeyFingerprint { get; }

    public static string ComputeKeyFingerprint(string wrappedCipherKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wrappedCipherKey);
        var bytes = Encoding.UTF8.GetBytes(wrappedCipherKey);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static IReadOnlyList<string> FreezeDistinct(
        IReadOnlyList<string> values,
        string parameterName)
    {
        var copy = new string[values.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var value in values)
        {
            if (value is null)
                throw new ArgumentException("Identifiers cannot contain null values.", parameterName);
            if (!seen.Add(value))
                continue;
            copy[count++] = value;
        }

        if (count != copy.Length)
            Array.Resize(ref copy, count);
        return Array.AsReadOnly(copy);
    }
}

public sealed record CipherCreatePending(
    CipherCreateWitness Witness,
    bool CommitConfirmed,
    string? ExpectedCipherId);

public sealed record CipherCreateReconciliationResult(
    CipherCreateReconciliationStatus Status,
    string? ConfirmedId,
    IReadOnlyList<string> CandidateIds);

public sealed record CipherCreateObservation(
    string Id,
    CipherType CipherType,
    string? OrganizationId,
    IReadOnlyList<string> CollectionIds,
    string? WrappedKey)
{
    public static CipherCreateObservation FromCipher(Cipher cipher)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        return new(
            cipher.Id,
            cipher.Type,
            cipher.OrganizationId,
            cipher.CollectionIds,
            cipher.Key);
    }
}

public static class CipherCreateReconciler
{
    public static CipherCreateReconciliationResult Reconcile(
        CipherCreatePending pending,
        string accountId,
        ServerEndpoints endpoints,
        IEnumerable<CipherCreateObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(pending.Witness);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(observations);

        var witness = pending.Witness;
        if (!string.Equals(witness.AccountId, accountId, StringComparison.Ordinal)
            || witness.Endpoints != endpoints)
        {
            return Result(CipherCreateReconciliationStatus.SessionMismatch);
        }

        var baselineIds = witness.BaselineCipherIds.ToHashSet(StringComparer.Ordinal);
        var keyMatches = observations
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.WrappedKey)
                && string.Equals(
                    CipherCreateWitness.ComputeKeyFingerprint(candidate.WrappedKey),
                    witness.KeyFingerprint,
                    StringComparison.Ordinal))
            .ToArray();
        var candidates = keyMatches
            .Where(candidate => !baselineIds.Contains(candidate.Id))
            .ToArray();
        var candidateIds = FreezeIds(candidates.Select(candidate => candidate.Id));

        if (candidates.Length == 0)
        {
            return keyMatches.Length == 0
                ? Result(CipherCreateReconciliationStatus.NotFound)
                : new(
                    CipherCreateReconciliationStatus.SnapshotMismatch,
                    ConfirmedId: null,
                    FreezeIds(keyMatches.Select(candidate => candidate.Id)));
        }

        if (candidates.Length > 1)
        {
            return new(
                CipherCreateReconciliationStatus.Ambiguous,
                ConfirmedId: null,
                candidateIds);
        }

        var candidate = candidates[0];
        var expectedCollections = witness.CollectionIds.ToHashSet(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(candidate.Id)
            || candidate.CipherType != witness.CipherType
            || !string.Equals(
                NormalizeOrganizationId(candidate.OrganizationId),
                NormalizeOrganizationId(witness.OrganizationId),
                StringComparison.Ordinal)
            || !candidate.CollectionIds.ToHashSet(StringComparer.Ordinal).SetEquals(expectedCollections)
            || (!string.IsNullOrWhiteSpace(pending.ExpectedCipherId)
                && !string.Equals(candidate.Id, pending.ExpectedCipherId, StringComparison.Ordinal)))
        {
            return new(
                CipherCreateReconciliationStatus.SnapshotMismatch,
                ConfirmedId: null,
                candidateIds);
        }

        return new(
            CipherCreateReconciliationStatus.Confirmed,
            candidate.Id,
            candidateIds);
    }

    private static string? NormalizeOrganizationId(string? organizationId) =>
        string.IsNullOrWhiteSpace(organizationId) ? null : organizationId;

    private static IReadOnlyList<string> FreezeIds(IEnumerable<string> ids) =>
        Array.AsReadOnly(ids.ToArray());

    private static CipherCreateReconciliationResult Result(
        CipherCreateReconciliationStatus status) =>
        new(status, ConfirmedId: null, CandidateIds: Array.Empty<string>());
}
