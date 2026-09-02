using Core.Enums;

namespace Core.Models;

public sealed class CipherPlacementBaseline
{
    private readonly IReadOnlyList<string> _collectionIds;

    public CipherPlacementBaseline(
        string cipherId,
        CipherType cipherType,
        DateTimeOffset revisionDate,
        string? organizationId,
        IEnumerable<string> collectionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cipherId);
        ArgumentNullException.ThrowIfNull(collectionIds);

        CipherId = cipherId;
        CipherType = cipherType;
        RevisionDate = revisionDate;
        OrganizationId = string.IsNullOrWhiteSpace(organizationId) ? null : organizationId;
        _collectionIds = Array.AsReadOnly(collectionIds.ToArray());
    }

    public string CipherId { get; }
    public CipherType CipherType { get; }
    public DateTimeOffset RevisionDate { get; }
    public string? OrganizationId { get; }
    public IReadOnlyList<string> CollectionIds => _collectionIds;

    public bool Matches(Cipher cipher) =>
        string.Equals(CipherId, cipher.Id, StringComparison.Ordinal)
        && CipherType == cipher.Type
        && RevisionDate == cipher.RevisionDate
        && string.Equals(OrganizationId, cipher.OrganizationId, StringComparison.Ordinal)
        && _collectionIds.ToHashSet(StringComparer.Ordinal).SetEquals(cipher.CollectionIds);

    public static CipherPlacementBaseline Capture(Cipher cipher)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        return new CipherPlacementBaseline(
            cipher.Id,
            cipher.Type,
            cipher.RevisionDate,
            cipher.OrganizationId,
            cipher.CollectionIds);
    }
}
