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

    private readonly IVaultApiClientProvider _clients;
    private readonly AttachmentCryptoService _crypto;
    private readonly VaultDecryptor _decryptor;
    private readonly VaultSession _session;
    private readonly ISyncService _sync;

    public AttachmentService(
        IVaultApiClientProvider clients,
        AttachmentCryptoService crypto,
        VaultDecryptor decryptor,
        VaultSession session,
        ISyncService sync)
    {
        _clients = clients;
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

        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IAttachmentApiClient>(lease);

        var dto = await api.GetCipherAsync(cipherId, linked.Token);
        var itemKey = _decryptor.ResolveItemKey(dto, userKey);

        // 每附件随机 64 字节 attKey,仅在本方法内用于加密文件名/文件体并包裹进 key 字段;
        // 用 using 保证用完清零(attKey 是新生成的独立密钥,不别名 itemKey/userKey)。
        using var attKey = _crypto.GenerateAttachmentKey();
        var encName = _crypto.EncryptFileName(fileName, attKey);
        var buffer = _crypto.EncryptFile(plaintext, attKey);
        var wrapped = _crypto.WrapKey(attKey, itemKey);

        var resp = await api.CreateAttachmentV2Async(
            cipherId, new AttachmentUploadRequest(wrapped, encName, buffer.LongLength), linked.Token);

        // 两步上传:第一步(post_attachment_v2)已在服务端落库一条附件记录,之后才传文件体。
        // 第二步(文件体)失败或被取消时,若不清理会留下下不动的孤儿附件记录、持续占用配额。
        // 因此包裹第二步:任何异常(含 OperationCanceledException)都 best-effort 删除刚建的记录,再原样重抛。
        try
        {
            await api.UploadAttachmentDataAsync(resp.Url, encName, buffer, linked.Token);
        }
        catch
        {
            // 补偿删除用 CancellationToken.None:即便原始失败源自 ct 取消,清理仍须执行完成。
            // 删除自身失败绝不能掩盖原始异常——吞掉补偿异常,由下方 throw 原样重抛原始异常。
            try
            {
                await api.DeleteAttachmentAsync(cipherId, resp.AttachmentId, CancellationToken.None);
            }
            catch
            {
                // best-effort:补偿删除失败无法进一步处理,忽略以保真原始异常。
            }

            throw;
        }

        _session.ThrowIfExpired(lease);
        await ResyncAsync(ct);
        return CurrentAttachments(cipherId);
    }

    public async Task<byte[]> DownloadAsync(string cipherId, string attachmentId, CancellationToken ct = default)
    {
        var userKey = RequireUserKey();
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IAttachmentApiClient>(lease);
        var dto = await api.GetCipherAsync(cipherId, linked.Token);
        var itemKey = _decryptor.ResolveItemKey(dto, userKey);

        var att = dto.Attachments?.FirstOrDefault(a => a.Id == attachmentId)
            ?? throw new InvalidOperationException("Attachment not found.");
        var attKey = string.IsNullOrEmpty(att.Key)
            ? itemKey
            : _crypto.UnwrapKey(att.Key, itemKey);
        var url = att.Url ?? throw new InvalidOperationException("Attachment url missing.");

        var bytes = await api.DownloadAttachmentBytesAsync(url, linked.Token);
        _session.ThrowIfExpired(lease);
        return _crypto.DecryptFile(bytes, attKey);
    }

    public async Task<IReadOnlyList<CipherAttachment>> DeleteAsync(
        string cipherId, string attachmentId, CancellationToken ct = default)
    {
        RequireUserKey();
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IAttachmentApiClient>(lease);
        await api.DeleteAttachmentAsync(cipherId, attachmentId, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(ct);
        return CurrentAttachments(cipherId);
    }

    private IReadOnlyList<CipherAttachment> CurrentAttachments(string cipherId) =>
        _session.Ciphers.FirstOrDefault(c => c.Id == cipherId)?.Attachments
            ?? Array.Empty<CipherAttachment>();

    private SymmetricCryptoKey RequireUserKey() =>
        _session.UserKey ?? throw new InvalidOperationException("Vault is locked.");

    // 写后整库重同步:写已在服务端成功,若刷新失败则本地快照可能过期。
    // 包装为 VaultRefreshException 让上层区分"写失败"与"写成功但刷新失败"。
    private async Task ResyncAsync(CancellationToken ct)
    {
        try
        {
            await _sync.SyncAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new VaultRefreshException("附件已更新,但刷新本地数据失败,请手动刷新。", ex);
        }
    }
}
