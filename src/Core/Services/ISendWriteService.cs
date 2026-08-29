using Core.Models;
using Core.Session;

namespace Core.Services;

public interface ISendWriteService
{
    Task<SendMutationReceipt> SaveTextSendAsync(
        SendDraftModel draft,
        VaultSessionLease expectedSession,
        CancellationToken ct = default);

    // File writes expose distinct create, metadata-only update, and replacement operations. A
    // replacement preserves the new version when deletion of the old version cannot be confirmed.
    Task<SendMutationReceipt> CreateFileSendAsync(
        SendDraftModel draft,
        SendFileSource file,
        VaultSessionLease expectedSession,
        CancellationToken ct = default);

    Task<SendMutationReceipt> UpdateFileSendMetadataAsync(
        SendDraftModel draft,
        VaultSessionLease expectedSession,
        CancellationToken ct = default);

    Task<SendMutationReceipt> ReplaceFileSendAsync(
        string existingSendId,
        SendDraftModel draft,
        SendFileSource file,
        VaultSessionLease expectedSession,
        CancellationToken ct = default);

    Task<SendMutationReceipt> DeleteSendAsync(
        string sendId,
        VaultSessionLease expectedSession,
        CancellationToken ct = default);
}
