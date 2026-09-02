namespace Core.Models;

public sealed record VaultCapabilities(
    bool CanWriteNewCipherTypes,
    bool SupportsArchiveProtocol,
    bool HasArchiveEntitlement,
    bool SupportsTdeV2JitProvisioning = false)
{
    public static VaultCapabilities None { get; } = new(false, false, false, false);

    public bool CanArchive => SupportsArchiveProtocol && HasArchiveEntitlement;

    public bool CanUnarchive => SupportsArchiveProtocol;
}
