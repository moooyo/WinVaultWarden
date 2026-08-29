using Core.Session;

namespace Vault;

/// <summary>
/// Deletes attachment metadata with the exact authorization binding used by its successful create
/// request. This narrow capability remains usable after the originating vault lease is invalidated.
/// </summary>
public interface IAttachmentCompensationClient
{
    Task DeleteAsync(
        AccessTokenBinding binding,
        string cipherId,
        string attachmentId,
        CancellationToken ct = default);
}
