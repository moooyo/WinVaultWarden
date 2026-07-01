using Api;
using Api.Dtos;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

// 附件子系统写编排:加密文件体/文件名 → v2 两步上传 → 整库重同步。
// 加密遵循 Bitwarden 附件方案:每附件独立 64 字节 attKey,attKey 用条目有效密钥(itemKey)包裹后放入 key 字段。
// 旧格式附件(att.Key 为空):文件名与文件体直接用 itemKey 加解密(无独立附件密钥)。
public sealed class AttachmentService : IAttachmentService
{
    public const long MaxPlaintextBytes = Core.Services.AttachmentLimits.MaxPlaintextBytes;

    private readonly IAttachmentApiClient _api;
    private readonly AttachmentCryptoService _crypto;
    private readonly VaultDecryptor _decryptor;
    private readonly VaultSession _session;
    private readonly ISyncService _sync;

    public AttachmentService(
        IAttachmentApiClient api,
        AttachmentCryptoService crypto,
        VaultDecryptor decryptor,
        VaultSession session,
        ISyncService sync)
    {
        _api = api;
        _crypto = crypto;
        _decryptor = decryptor;
        _session = session;
        _sync = sync;
    }

    public async Task<IReadOnlyList<CipherAttachment>> UploadAsync(
        string cipherId, string fileName, byte[] plaintext, CancellationToken ct = default)
    {
        var userKey = RequireUserKey();
        if (plaintext.LongLength > MaxPlaintextBytes)
            throw new AttachmentTooLargeException(plaintext.LongLength, MaxPlaintextBytes);

        var dto = await _api.GetCipherAsync(cipherId, ct);
        var itemKey = _decryptor.ResolveItemKey(dto, userKey);

        var attKey = _crypto.GenerateAttachmentKey();
        var encName = _crypto.EncryptFileName(fileName, attKey);
        var buffer = _crypto.EncryptFile(plaintext, attKey);
        var wrapped = _crypto.WrapKey(attKey, itemKey);

        var resp = await _api.CreateAttachmentV2Async(
            cipherId, new AttachmentUploadRequest(wrapped, encName, buffer.LongLength), ct);
        await _api.UploadAttachmentDataAsync(resp.Url, encName, buffer, ct);

        await _sync.SyncAsync(ct);
        return CurrentAttachments(cipherId);
    }

    public async Task<byte[]> DownloadAsync(string cipherId, string attachmentId, CancellationToken ct = default)
    {
        var userKey = RequireUserKey();
        var dto = await _api.GetCipherAsync(cipherId, ct);
        var itemKey = _decryptor.ResolveItemKey(dto, userKey);

        var att = dto.Attachments?.FirstOrDefault(a => a.Id == attachmentId)
            ?? throw new InvalidOperationException("Attachment not found.");
        var attKey = string.IsNullOrEmpty(att.Key)
            ? itemKey
            : _crypto.UnwrapKey(att.Key, itemKey);
        var url = att.Url ?? throw new InvalidOperationException("Attachment url missing.");

        var bytes = await _api.DownloadAttachmentBytesAsync(url, ct);
        return _crypto.DecryptFile(bytes, attKey);
    }

    public async Task<IReadOnlyList<CipherAttachment>> DeleteAsync(
        string cipherId, string attachmentId, CancellationToken ct = default)
    {
        RequireUserKey();
        await _api.DeleteAttachmentAsync(cipherId, attachmentId, ct);
        await _sync.SyncAsync(ct);
        return CurrentAttachments(cipherId);
    }

    private IReadOnlyList<CipherAttachment> CurrentAttachments(string cipherId) =>
        _session.Ciphers.FirstOrDefault(c => c.Id == cipherId)?.Attachments
            ?? Array.Empty<CipherAttachment>();

    private SymmetricCryptoKey RequireUserKey() =>
        _session.UserKey ?? throw new InvalidOperationException("Vault is locked.");
}
