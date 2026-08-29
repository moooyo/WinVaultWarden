using System.Diagnostics;
using Api;
using Api.Dtos;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;

namespace Vault;

// Coordinates streaming attachment encryption, two-step upload, authenticated download, and
// session-bound refresh. Modern attachments own a separate key; legacy attachments borrow the item
// key. Every owned key is disposed within the operation that created it.
public sealed class AttachmentService : IAttachmentService
{
    public const long MaxPlaintextBytes = Core.Services.AttachmentLimits.MaxPlaintextBytes;

    private readonly IVaultApiClientProvider _clients;
    private readonly AttachmentCryptoService _crypto;
    private readonly VaultDecryptor _decryptor;
    private readonly VaultSession _session;
    private readonly ISyncService _sync;
    private readonly IAttachmentCompensationClient _compensation;
    private readonly ITransferIdleTimeoutPolicy _timeoutPolicy;

    public AttachmentService(
        IVaultApiClientProvider clients,
        AttachmentCryptoService crypto,
        VaultDecryptor decryptor,
        VaultSession session,
        ISyncService sync,
        IAttachmentCompensationClient compensation,
        ITransferIdleTimeoutPolicy timeoutPolicy)
    {
        _clients = clients;
        _crypto = crypto;
        _decryptor = decryptor;
        _session = session;
        _sync = sync;
        _compensation = compensation;
        _timeoutPolicy = timeoutPolicy;
    }

    public async Task<IReadOnlyList<CipherAttachment>> UploadAsync(
        string cipherId,
        ReopenableFileSource file,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cipherId);
        ArgumentNullException.ThrowIfNull(file);
        var lease = _session.CaptureLease();
        if (!_session.TryCopyUserKey(lease, out var copiedKey))
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey!;
        if (file.Length < 0 || file.Length > MaxPlaintextBytes)
            throw new AttachmentTooLargeException(file.Length, MaxPlaintextBytes);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IAttachmentApiClient>(lease);

        var dto = await api.GetCipherAsync(cipherId, linked.Token);
        _session.ThrowIfExpired(lease);
        using var ownedItemKey = string.IsNullOrWhiteSpace(dto.Key)
            ? null
            : _decryptor.ResolveItemKey(dto, userKey);
        var itemKey = ownedItemKey ?? userKey;

        using var attKey = _crypto.GenerateAttachmentKey();
        var encName = _crypto.EncryptFileName(file.FileName, attKey);
        var upload = await _crypto.PrepareFileUploadAsync(
            file,
            attKey,
            _timeoutPolicy,
            linked.Token);
        var wrapped = _crypto.WrapKey(attKey, itemKey);

        _session.ThrowIfExpired(lease);
        var receipt = await api.CreateAttachmentV2Async(
            cipherId,
            new AttachmentUploadRequest(wrapped, encName, upload.Length),
            linked.Token);
        var response = receipt.Value;
        try
        {
            _session.ThrowIfExpired(lease);
            await api.UploadAttachmentDataAsync(
                response.FileUploadType,
                response.Url,
                cipherId,
                response.AttachmentId,
                encName,
                upload,
                linked.Token);
            _session.ThrowIfExpired(lease);
            linked.Token.ThrowIfCancellationRequested();
        }
        catch (Exception uploadError)
        {
            await DeleteOrThrowCompensationAsync(
                receipt.Authorization,
                cipherId,
                response.AttachmentId,
                uploadError);
            throw;
        }

        return await RefreshCommittedUploadAsync(lease, cipherId, linked.Token);
    }

    public async Task<long> DownloadAsync(
        string cipherId,
        string attachmentId,
        Stream plaintextDestination,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cipherId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);
        ArgumentNullException.ThrowIfNull(plaintextDestination);
        var lease = _session.CaptureLease();
        if (!_session.TryCopyUserKey(lease, out var copiedKey))
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey!;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IAttachmentApiClient>(lease);
        var dto = await api.GetCipherAsync(cipherId, linked.Token);
        _session.ThrowIfExpired(lease);
        using var ownedItemKey = string.IsNullOrWhiteSpace(dto.Key)
            ? null
            : _decryptor.ResolveItemKey(dto, userKey);
        var itemKey = ownedItemKey ?? userKey;

        var att = dto.Attachments?.FirstOrDefault(a => a.Id == attachmentId)
            ?? throw new InvalidOperationException("Attachment not found.");
        using var ownedAttachmentKey = string.IsNullOrEmpty(att.Key)
            ? null
            : _crypto.UnwrapKey(att.Key, itemKey);
        var attKey = ownedAttachmentKey ?? itemKey;
        var url = att.Url ?? throw new InvalidOperationException("Attachment url missing.");

        await using var download = await api.OpenAttachmentDownloadAsync(url, linked.Token);
        var written = await _crypto.DecryptFileAsync(
            download.Stream,
            plaintextDestination,
            attKey,
            download.ContentLength,
            _timeoutPolicy,
            linked.Token);
        _session.ThrowIfExpired(lease);
        return written;
    }

    public async Task<IReadOnlyList<CipherAttachment>> DeleteAsync(
        string cipherId, string attachmentId, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        if (!_session.TryCopyUserKey(lease, out var copiedKey))
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey!;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IAttachmentApiClient>(lease);
        await api.DeleteAttachmentAsync(cipherId, attachmentId, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, linked.Token);
        return CurrentAttachments(lease, cipherId);
    }

    private IReadOnlyList<CipherAttachment> CurrentAttachments(VaultSessionLease lease, string cipherId) =>
        _session.GetCiphers(lease).FirstOrDefault(c => c.Id == cipherId)?.Attachments
            ?? Array.Empty<CipherAttachment>();

    private async Task DeleteOrThrowCompensationAsync(
        AccessTokenBinding binding,
        string cipherId,
        string attachmentId,
        Exception operationError)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await _compensation.DeleteAsync(binding, cipherId, attachmentId, cleanup.Token);
        }
        catch (Exception cleanupError)
        {
            Trace.TraceError(
                "Attachment compensation delete failed for cipher {0}, attachment {1}: {2}",
                cipherId,
                attachmentId,
                cleanupError.GetType().Name);
            throw new AttachmentUploadCompensationException(
                cipherId,
                attachmentId,
                operationError,
                cleanupError);
        }
    }

    private async Task ResyncAsync(VaultSessionLease lease, CancellationToken ct)
    {
        try
        {
            await _sync.SyncAsync(lease, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new VaultRefreshException("附件已更新,但刷新本地数据失败,请手动刷新。", ex);
        }
    }

    private async Task<IReadOnlyList<CipherAttachment>> RefreshCommittedUploadAsync(
        VaultSessionLease lease,
        string cipherId,
        CancellationToken ct)
    {
        try
        {
            await _sync.SyncAsync(lease, ct);
            return CurrentAttachments(lease, cipherId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new VaultRefreshException(
                "附件已保存，但本地数据刷新失败，需手动同步。",
                ex);
        }
    }
}
