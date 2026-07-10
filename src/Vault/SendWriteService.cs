using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;

namespace Vault;

// Send 写编排:生成/复用 seed → 派生 cryptoKey → 加密字段 → 用 UserKey 包裹 seed → 组 SendRequest → 创建/更新。
// 文件类型(Task 12):以固定 IV 两遍式常量内存流加密成 EncArrayBuffer,fileLength=加密后精确字节数,
// 先 v2 创建拿上传 URL(vaultwarden src/api/core/sends.rs:303-355 先存元数据记录),再 streaming multipart 上传;
// 上传失败/取消用【独立 15s 令牌】补偿删除孤儿记录(delete 路由 sends.rs:668),清理再失败抛 SendUploadCompensationException。
// 替换 = 先建+传新件再删旧件;删旧失败则回滚删新件。元数据更新只发 PUT,绝不重传文件字节(put_send 忽略 File,sends.rs:631-645)。
// 更新(文本/文件元数据)必须复用 draft.Key 解出的原 seed,绝不轮换(见 ResolveSeed);
// 密码变更是显式三态(SendPasswordChange.Keep/Set/Remove),Remove 必须先等元数据 PUT 成功,
// 再调用专用 remove-password 路由(见 vaultwarden src/api/core/sends.rs:686-705),任一步失败不得伪装成功。
public sealed class SendWriteService : ISendWriteService
{
    private readonly IVaultApiClientProvider _clients;
    private readonly SendCryptoService _crypto;
    private readonly VaultSession _session;

    public SendWriteService(IVaultApiClientProvider clients, SendCryptoService crypto, VaultSession session)
    {
        _clients = clients;
        _crypto = crypto;
        _session = session;
    }

    public async Task SaveTextSendAsync(SendDraftModel draft, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        if (!_session.TryCopyUserKey(lease, out var copiedKey))
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey!;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ISendApiClient>(lease);
        var seed = ResolveSeed(draft, userKey);
        try
        {
            using var cryptoKey = _crypto.DeriveCryptoKey(seed);
            var request = BuildBaseRequest(draft, seed, cryptoKey, userKey);
            request.Type = (int)SendType.Text;
            request.Text = new SendTextRequest(
                Text: draft.TextContent is null ? null : _crypto.EncryptField(draft.TextContent, cryptoKey),
                Hidden: draft.TextHidden);

            if (string.IsNullOrEmpty(draft.Id))
            {
                if (draft.PasswordChange is SendPasswordChange.Remove)
                    throw new InvalidOperationException("新 Send 不能移除尚不存在的密码。");
                await api.CreateTextSendAsync(request, linked.Token);
            }
            else
            {
                await api.UpdateSendAsync(draft.Id, request, linked.Token);
                _session.ThrowIfExpired(lease);
                if (draft.PasswordChange is SendPasswordChange.Remove)
                    await api.RemoveSendPasswordAsync(draft.Id, linked.Token);
            }
            _session.ThrowIfExpired(lease);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    // 创建新文件 Send:先在任何 API 前校验明文上限,再生成新 seed;v2 建记录拿上传 URL → streaming 上传。
    // 上传失败/取消一律用【独立 15s 令牌】补偿删除刚建的孤儿记录(见 CreateFileCoreAsync)。
    public async Task<SendFileWriteResult> CreateFileSendAsync(SendDraftModel draft, SendFileSource file, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(draft.Id))
            throw new InvalidOperationException("CreateFileSendAsync requires a new Send draft.");
        SendFileSource.ValidateLength(file.Length);
        var lease = _session.CaptureLease();
        if (!_session.TryCopyUserKey(lease, out var copiedKey))
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var seed = _crypto.GenerateSeed();
        try
        {
            var api = _clients.GetAuthenticated<ISendApiClient>(lease);
            return await CreateFileCoreAsync(
                lease, api, userKey!, draft, file, seed, replacedExisting: false, ct: linked.Token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    // 替换既有文件 Send:服务端无法原地替换文件字节,故【先建+传新件,再删旧件】。
    // 复用旧 draft.Key 解出的原 seed 加密新件(WithId(null) 让其按"创建"处理)。
    // 删旧失败 → 回滚删新件(独立令牌补偿),并把原始删旧异常抛出;任何一步失败都不得伪装成功。
    public async Task<SendFileWriteResult> ReplaceFileSendAsync(string existingSendId, SendDraftModel draft, SendFileSource file, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingSendId);
        SendFileSource.ValidateLength(file.Length);
        var lease = _session.CaptureLease();
        if (!_session.TryCopyUserKey(lease, out var copiedKey))
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var seed = ResolveSeed(draft, userKey!);
        try
        {
            var api = _clients.GetAuthenticated<ISendApiClient>(lease);
            var created = await CreateFileCoreAsync(
                lease, api, userKey!, draft.WithId(null), file, seed, replacedExisting: true, ct: linked.Token);
            try
            {
                await api.DeleteSendAsync(existingSendId, linked.Token);
                _session.ThrowIfExpired(lease);
                return created;
            }
            catch (Exception deleteOldError)
            {
                await DeleteOrThrowCompensationAsync(api, created.SendId, deleteOldError);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    // 文件创建的公共核心(创建/替换共用):任何 API 前再次校验长度,以固定 IV 两遍式常量内存流加密上传;
    // 上传失败/取消用独立令牌补偿删除,清理再失败则抛 SendUploadCompensationException(暴露孤儿 SendId)。
    private async Task<SendFileWriteResult> CreateFileCoreAsync(
        VaultSessionLease lease,
        ISendApiClient api,
        SymmetricCryptoKey userKey,
        SendDraftModel draft,
        SendFileSource file,
        byte[] seed,
        bool replacedExisting,
        CancellationToken ct)
    {
        SendFileSource.ValidateLength(file.Length);
        using var cryptoKey = _crypto.DeriveCryptoKey(seed);
        var encryptedName = _crypto.EncryptField(file.FileName, cryptoKey);
        var upload = await _crypto.PrepareFileUploadAsync(file, cryptoKey, ct);
        var request = BuildBaseRequest(draft, seed, cryptoKey, userKey);
        request.Type = (int)SendType.File;
        request.File = new SendFileRequest(encryptedName);
        request.FileLength = checked((int)upload.Length);
        var response = await api.CreateFileSendV2Async(request, ct);
        try
        {
            await api.UploadSendFileAsync(response.Url, encryptedName, upload, ct);
        }
        catch (Exception uploadError)
        {
            await DeleteOrThrowCompensationAsync(api, response.SendResponse.Id, uploadError);
            throw;
        }
        _session.ThrowIfExpired(lease);
        return new SendFileWriteResult(response.SendResponse.Id, response.SendResponse.AccessId, replacedExisting);
    }

    // 用【独立 15s 令牌】删除孤儿 Send:与调用方令牌解耦,即使调用方已取消也能完成清理。
    // 清理成功 → 原始错误由调用方 rethrow;清理失败 → 抛可操作的 SendUploadCompensationException(不泄露密钥)。
    private static async Task DeleteOrThrowCompensationAsync(ISendApiClient api, string sendId, Exception originalError)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await api.DeleteSendAsync(sendId, cleanup.Token);
        }
        catch (Exception cleanupError)
        {
            Trace.TraceError(
                "Send compensation delete failed for id {0}: {1}", sendId, cleanupError.GetType().Name);
            throw new SendUploadCompensationException(sendId, originalError, cleanupError);
        }
    }

    // 文本/文件元数据更新的统一入口:复用同一 ResolveSeed/BuildBaseRequest,File/FileLength 保持 null
    // (文件字节在服务端不可变)。Remove 密码时,先等元数据 PUT 成功,再调用专用 remove-password 路由;
    // 中途任一步失败都不得吞掉异常伪装成功。
    public async Task UpdateFileSendMetadataAsync(SendDraftModel draft, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(draft.Id))
            throw new InvalidOperationException("UpdateFileSendMetadataAsync 只用于更新既有 Send。");

        var lease = _session.CaptureLease();
        if (!_session.TryCopyUserKey(lease, out var copiedKey))
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey!;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ISendApiClient>(lease);
        var seed = ResolveSeed(draft, userKey);
        try
        {
            using var cryptoKey = _crypto.DeriveCryptoKey(seed);
            var request = BuildBaseRequest(draft, seed, cryptoKey, userKey);
            request.Type = (int)SendType.File;
            // File / FileLength 更新时被服务端忽略(文件不可变),保持 null。

            await api.UpdateSendAsync(draft.Id, request, linked.Token);
            _session.ThrowIfExpired(lease);
            if (draft.PasswordChange is SendPasswordChange.Remove)
                await api.RemoveSendPasswordAsync(draft.Id, linked.Token);
            _session.ThrowIfExpired(lease);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public async Task DeleteSendAsync(string sendId, CancellationToken ct = default)
    {
        RequireUserKey();
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ISendApiClient>(lease);
        await api.DeleteSendAsync(sendId, linked.Token);
        _session.ThrowIfExpired(lease);
    }

    // 解析本次写入应使用的 seed:创建(Id 为空)生成新 seed;更新必须从 draft.Key 解包复用原 seed,
    // 绝不为更新生成新 seed —— 否则服务端仍以旧 seed 加密的字段(文件名/文件字节/文本)将永久无法解密。
    private byte[] ResolveSeed(SendDraftModel draft, SymmetricCryptoKey userKey)
    {
        if (string.IsNullOrEmpty(draft.Id))
            return _crypto.GenerateSeed();
        if (string.IsNullOrWhiteSpace(draft.Key))
            throw new InvalidOperationException("更新 Send 需要原始 wrapped key。");
        return _crypto.UnwrapSeed(draft.Key, userKey);
    }

    // 密码变更三态 → 服务端 password 字段语义:Keep 不传(null,服务端保留原值);Set 传新证明;
    // Remove 也不传(null),移除动作由调用方随后另发专用 remove-password 请求完成。
    private string? ResolvePasswordProof(SendPasswordChange change, byte[] seed) => change switch
    {
        SendPasswordChange.Keep => null,
        SendPasswordChange.Set set => _crypto.ComputePasswordProof(set.Password, seed),
        SendPasswordChange.Remove => null,
        _ => throw new ArgumentOutOfRangeException(nameof(change)),
    };

    // 组装通用字段(name/notes/key/password 证明/到期/删除日期/标志位)。
    // 返回可变 SendRequest 以便调用方补填 Type/Text/File/FileLength。
    private MutableSendRequest BuildBaseRequest(
        SendDraftModel draft, byte[] seed, SymmetricCryptoKey cryptoKey, SymmetricCryptoKey userKey)
    {
        return new MutableSendRequest
        {
            Id = string.IsNullOrEmpty(draft.Id) ? null : draft.Id,
            Name = _crypto.EncryptField(draft.Name, cryptoKey),
            Notes = draft.Notes is null ? null : _crypto.EncryptField(draft.Notes, cryptoKey),
            Key = _crypto.WrapSeed(seed, userKey),
            Password = ResolvePasswordProof(draft.PasswordChange, seed),
            MaxAccessCount = draft.MaxAccessCount,
            ExpirationDate = ToIso(draft.ExpirationDate),
            DeletionDate = ToIso(draft.DeletionDate)!,
            Disabled = draft.Disabled,
            HideEmail = draft.HideEmail,
        };
    }

    private static string? ToIso(DateTimeOffset? value) =>
        value is null ? null : value.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private SymmetricCryptoKey RequireUserKey() =>
        _session.UserKey ?? throw new InvalidOperationException("Vault is locked.");

    // 内部可变构建器,构建完毕后隐式转换为不可变 SendRequest。
    private sealed class MutableSendRequest
    {
        public int Type { get; set; }
        public string Key { get; set; } = string.Empty;
        public string? Password { get; set; }
        public int? MaxAccessCount { get; set; }
        public string? ExpirationDate { get; set; }
        public string DeletionDate { get; set; } = string.Empty;
        public bool Disabled { get; set; }
        public bool? HideEmail { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Notes { get; set; }
        public SendTextRequest? Text { get; set; }
        public SendFileRequest? File { get; set; }
        public int? FileLength { get; set; }
        public string? Id { get; set; }

        public static implicit operator SendRequest(MutableSendRequest m) => new()
        {
            Type = m.Type,
            Key = m.Key,
            Password = m.Password,
            MaxAccessCount = m.MaxAccessCount,
            ExpirationDate = m.ExpirationDate,
            DeletionDate = m.DeletionDate,
            Disabled = m.Disabled,
            HideEmail = m.HideEmail,
            Name = m.Name,
            Notes = m.Notes,
            Text = m.Text,
            File = m.File,
            FileLength = m.FileLength,
            Id = m.Id,
        };
    }
}
