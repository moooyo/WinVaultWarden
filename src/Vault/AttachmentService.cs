using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
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
    private readonly OrganizationKeyResolver _organizationKeys;

    public AttachmentService(
        IVaultApiClientProvider clients,
        AttachmentCryptoService crypto,
        VaultDecryptor decryptor,
        VaultSession session,
        ISyncService sync,
        IAttachmentCompensationClient compensation,
        ITransferIdleTimeoutPolicy timeoutPolicy,
        OrganizationKeyResolver? organizationKeys = null)
    {
        _clients = clients;
        _crypto = crypto;
        _decryptor = decryptor;
        _session = session;
        _sync = sync;
        _compensation = compensation;
        _timeoutPolicy = timeoutPolicy;
        _organizationKeys = organizationKeys ?? new OrganizationKeyResolver(new CryptoService());
    }

    public async Task<IReadOnlyList<CipherAttachment>> UploadAsync(
        string cipherId,
        ReopenableFileSource file,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cipherId);
        ArgumentNullException.ThrowIfNull(file);
        var lease = _session.CaptureLease();
        if (!_session.TryCopyUserKeyMaterial(lease, out var copiedKey))
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey!;
        if (file.Length < 0 || file.Length > MaxPlaintextBytes)
            throw new AttachmentTooLargeException(file.Length, MaxPlaintextBytes);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IAttachmentApiClient>(lease);
        var openingBaseline = CaptureUploadBaseline(lease, cipherId);

        var dto = await api.GetCipherAsync(cipherId, linked.Token);
        _session.ThrowIfExpired(lease);
        RequireMatchingEncryptionBaseline(cipherId, openingBaseline, dto);
        using var organizationKey = ResolveOrganizationKey(
            dto,
            lease,
            userKey,
            requireEdit: true,
            requireViewPassword: false);
        var legacyUserKey = userKey.Legacy;
        using var ownedItemKey = organizationKey is not null
            ? string.IsNullOrWhiteSpace(dto.Key)
                ? null
                : _decryptor.ResolveItemKey(dto, organizationKey)
            : legacyUserKey is not null
                ? string.IsNullOrWhiteSpace(dto.Key)
                    ? null
                    : _decryptor.ResolveItemKey(dto, legacyUserKey)
                : _decryptor.ResolveItemKey(dto, userKey.V2!);
        var itemKey = ownedItemKey
            ?? organizationKey
            ?? legacyUserKey
            ?? throw new CryptographicException("A V2 personal cipher is missing its item key.");

        using var attKey = _crypto.GenerateAttachmentKey();
        var encName = _crypto.EncryptFileName(file.FileName, attKey);
        var upload = await _crypto.PrepareFileUploadAsync(
            file,
            attKey,
            _timeoutPolicy,
            linked.Token);
        var wrapped = _crypto.WrapKey(attKey, itemKey);

        _session.ThrowIfExpired(lease);
        var currentBaseline = CaptureUploadBaseline(lease, cipherId);
        RequireCompatibleReplacement(cipherId, openingBaseline, currentBaseline);
        AuthenticatedWriteReceipt<AttachmentUploadV2Response> receipt;
        try
        {
            receipt = await api.CreateAttachmentV2Async(
                cipherId,
                new AttachmentUploadRequest(
                    wrapped,
                    encName,
                    upload.Length,
                    currentBaseline.RevisionDate.UtcDateTime),
                linked.Token);
        }
        catch (VaultWriteException ex) when (IsAttachmentRevisionConflict(ex))
        {
            throw new AttachmentRevisionConflictException(
                cipherId,
                currentBaseline.RevisionDate,
                "The attachment was not uploaded because the item revision is out of date. " +
                "Sync the vault and try again.",
                ex);
        }
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

    private static bool IsAttachmentRevisionConflict(VaultWriteException exception)
    {
        if (exception.StatusCode == HttpStatusCode.Conflict)
            return true;
        if (exception.StatusCode != HttpStatusCode.BadRequest)
            return false;

        if (exception.ValidationErrors?.Keys.Any(key =>
                string.Equals(
                    key,
                    "lastKnownRevisionDate",
                    StringComparison.OrdinalIgnoreCase)) == true)
        {
            return true;
        }

        return exception.Message.Contains("out of date", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("revision", StringComparison.OrdinalIgnoreCase);
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
        if (!_session.TryCopyUserKeyMaterial(lease, out var copiedKey))
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey!;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IAttachmentApiClient>(lease);
        var dto = await api.GetCipherAsync(cipherId, linked.Token);
        _session.ThrowIfExpired(lease);
        using var organizationKey = ResolveOrganizationKey(
            dto,
            lease,
            userKey,
            requireEdit: false,
            requireViewPassword: true);
        var legacyUserKey = userKey.Legacy;
        using var ownedItemKey = organizationKey is not null
            ? string.IsNullOrWhiteSpace(dto.Key)
                ? null
                : _decryptor.ResolveItemKey(dto, organizationKey)
            : legacyUserKey is not null
                ? string.IsNullOrWhiteSpace(dto.Key)
                    ? null
                    : _decryptor.ResolveItemKey(dto, legacyUserKey)
                : _decryptor.ResolveItemKey(dto, userKey.V2!);
        var itemKey = ownedItemKey
            ?? organizationKey
            ?? legacyUserKey
            ?? throw new CryptographicException("A V2 personal cipher is missing its item key.");

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
        if (!_session.TryCopyUserKeyMaterial(lease, out var copiedKey))
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey!;
        RequireAttachmentWriteAccess(lease, cipherId);
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

    private AttachmentUploadBaseline CaptureUploadBaseline(
        VaultSessionLease lease,
        string cipherId)
    {
        AttachmentUploadBaseline? baseline = null;
        var snapshotMatchesLease = false;
        var uniqueCipher = false;

        if (!_session.TryCommit(lease, () =>
            {
                var account = _session.Account;
                snapshotMatchesLease = string.Equals(
                        account.Id,
                        lease.AccountId,
                        StringComparison.Ordinal)
                    && account.Endpoints == lease.Endpoints;
                if (!snapshotMatchesLease)
                    return;

                Cipher? match = null;
                foreach (var candidate in _session.Ciphers)
                {
                    if (!string.Equals(candidate.Id, cipherId, StringComparison.Ordinal))
                        continue;
                    if (match is not null)
                    {
                        uniqueCipher = false;
                        return;
                    }
                    match = candidate;
                }

                uniqueCipher = match is not null;
                if (!uniqueCipher || match!.RevisionDate == default)
                    return;

                var cipher = match;
                baseline = new AttachmentUploadBaseline(
                    cipher.RevisionDate,
                    cipher.Key,
                    cipher.OrganizationId);
            }))
        {
            throw new OperationCanceledException(
                "Vault session lease expired before attachment metadata creation.",
                lease.CancellationToken);
        }

        if (!snapshotMatchesLease)
        {
            throw new AttachmentRevisionConflictException(
                cipherId,
                null,
                "The current vault snapshot does not belong to this account session. " +
                "Sync the vault and try again.");
        }

        if (!uniqueCipher || baseline is null)
        {
            throw new AttachmentRevisionConflictException(
                cipherId,
                null,
                "The item has no current revision in this vault snapshot. " +
                "Sync the vault and try again.");
        }

        return baseline;
    }

    private static void RequireMatchingEncryptionBaseline(
        string cipherId,
        AttachmentUploadBaseline baseline,
        CipherDto dto)
    {
        if (!string.Equals(dto.Id, cipherId, StringComparison.Ordinal)
            || !string.Equals(dto.Key, baseline.EncryptedKey, StringComparison.Ordinal)
            || !string.Equals(
                dto.OrganizationId,
                baseline.OrganizationId,
                StringComparison.Ordinal))
        {
            throw new AttachmentRevisionConflictException(
                cipherId,
                baseline.RevisionDate,
                "The item encryption baseline changed. Sync the vault and try again.");
        }
    }

    private static void RequireCompatibleReplacement(
        string cipherId,
        AttachmentUploadBaseline opening,
        AttachmentUploadBaseline current)
    {
        if (!string.Equals(opening.EncryptedKey, current.EncryptedKey, StringComparison.Ordinal)
            || !string.Equals(
                opening.OrganizationId,
                current.OrganizationId,
                StringComparison.Ordinal))
        {
            throw new AttachmentRevisionConflictException(
                cipherId,
                current.RevisionDate,
                "The item encryption baseline changed while preparing the attachment. " +
                "Sync the vault and try again.");
        }
    }

    private SymmetricCryptoKey? ResolveOrganizationKey(
        CipherDto dto,
        VaultSessionLease lease,
        VaultUserKey userKey,
        bool requireEdit,
        bool requireViewPassword)
    {
        if (string.IsNullOrWhiteSpace(dto.OrganizationId))
            return null;

        var current = _session.GetCiphers(lease).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, dto.Id, StringComparison.Ordinal)
            && string.Equals(candidate.OrganizationId, dto.OrganizationId, StringComparison.Ordinal));
        if (current is null
            || (requireEdit && (!current.CanEdit || dto.Edit == false))
            || (requireViewPassword && (!current.CanViewPassword || dto.ViewPassword == false)))
        {
            throw new OrganizationCipherOperationException(
                "The organization cipher does not permit this attachment operation.");
        }

        var organization = _session.Organizations.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, dto.OrganizationId, StringComparison.Ordinal))
            ?? throw new OrganizationCipherOperationException(
                "The organization is not available in the current vault snapshot.");
        _session.ThrowIfExpired(lease);
        if (!_session.TryGetEncryptedPrivateKey(lease, out var encryptedPrivateKey))
            throw new OperationCanceledException(lease.CancellationToken);
        return userKey.Legacy is { } legacy
            ? _organizationKeys.Resolve(organization, encryptedPrivateKey, legacy)
            : _organizationKeys.Resolve(organization, encryptedPrivateKey, userKey.V2!);
    }

    private void RequireAttachmentWriteAccess(VaultSessionLease lease, string cipherId)
    {
        var cipher = _session.GetCiphers(lease).FirstOrDefault(candidate => candidate.Id == cipherId);
        if (!string.IsNullOrWhiteSpace(cipher?.OrganizationId) && !cipher.CanEdit)
        {
            throw new OrganizationCipherOperationException(
                "The organization cipher does not permit attachment changes.");
        }
    }

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

    private sealed record AttachmentUploadBaseline(
        DateTimeOffset RevisionDate,
        string? EncryptedKey,
        string? OrganizationId);
}
