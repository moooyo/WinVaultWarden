using System.Globalization;
using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

// Send 写编排:生成/复用 seed → 派生 cryptoKey → 加密字段 → 用 UserKey 包裹 seed → 组 SendRequest → 创建/更新。
// 文件类型:把文件加密成 EncArrayBuffer,fileLength=buffer.Length,先 v2 创建拿上传 URL,再 multipart 上传。
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

    // Task 12 之前的过渡适配器:文件创建走 v2 create + upload;既有文件 Send 的更新(无论调用方是否
    // 重选了文件字节)一律转发到 UpdateFileSendMetadataAsync —— 服务端没有任何端点能替换既有 Send 的
    // 文件字节(put_send 收到 File=null 时忽略 file data,见 vaultwarden src/api/core/sends.rs:631-643),
    // 与 Bitwarden 官方客户端一致。Task 12 会拆出显式的 CreateFileSendAsync/ReplaceFileSendAsync。
    [Obsolete("Task 12 replaces this adapter with CreateFileSendAsync, UpdateFileSendMetadataAsync, and ReplaceFileSendAsync.")]
    public async Task SaveFileSendAsync(SendDraftModel draft, byte[] fileBytes, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(draft.Id))
        {
            await UpdateFileSendMetadataAsync(draft, ct);
            return;
        }

        var lease = _session.CaptureLease();
        if (!_session.TryCopyUserKey(lease, out var copiedKey))
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey!;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ISendApiClient>(lease);

        if (draft.PasswordChange is SendPasswordChange.Remove)
            throw new InvalidOperationException("新 Send 不能移除尚不存在的密码。");

        // 创建新文件 Send:v2 建记录拿上传 URL → multipart 上传加密字节。
        var seed = ResolveSeed(draft, userKey);
        try
        {
            using var cryptoKey = _crypto.DeriveCryptoKey(seed);

            var buffer = _crypto.EncryptToBuffer(fileBytes, cryptoKey);
            var encryptedFileName = _crypto.EncryptField(draft.FileName ?? string.Empty, cryptoKey);

            var request = BuildBaseRequest(draft, seed, cryptoKey, userKey);
            request.Type = (int)SendType.File;
            request.File = new SendFileRequest(FileName: encryptedFileName);
            request.FileLength = buffer.Length;

            var response = await api.CreateFileSendV2Async(request, linked.Token);
            await api.UploadSendFileAsync(response.Url, encryptedFileName, buffer, linked.Token);
            _session.ThrowIfExpired(lease);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
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

    public async Task<Send> RemovePasswordAsync(string sendId, CancellationToken ct = default)
    {
        var userKey = RequireUserKey();
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ISendApiClient>(lease);
        var dto = await api.RemoveSendPasswordAsync(sendId, linked.Token);
        _session.ThrowIfExpired(lease);
        return Decrypt(dto, userKey);
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

    private Send Decrypt(SendResponseDto dto, SymmetricCryptoKey userKey)
    {
        var seed = _crypto.UnwrapSeed(dto.Key!, userKey);
        var cryptoKey = _crypto.DeriveCryptoKey(seed);

        SendText? text = dto.Text is null
            ? null
            : new SendText(_crypto.DecryptField(dto.Text.Text, cryptoKey), dto.Text.Hidden);

        SendFile? file = null;
        if (dto.File is not null)
        {
            var fileName = _crypto.DecryptField(dto.File.FileName, cryptoKey) ?? string.Empty;
            var size = dto.File.Size ?? 0L;
            file = new SendFile(fileName, size, dto.File.SizeName, dto.File.Id);
        }

        return new Send
        {
            Id = dto.Id,
            AccessId = dto.AccessId,
            Type = (SendType)dto.Type,
            Name = _crypto.DecryptField(dto.Name, cryptoKey) ?? string.Empty,
            Notes = _crypto.DecryptField(dto.Notes, cryptoKey),
            Text = text,
            File = file,
            MaxAccessCount = dto.MaxAccessCount,
            AccessCount = dto.AccessCount,
            ExpirationDate = dto.ExpirationDate,
            DeletionDate = dto.DeletionDate,
            Disabled = dto.Disabled,
            HideEmail = dto.HideEmail,
            HasPassword = !string.IsNullOrEmpty(dto.Password),
            Key = dto.Key,
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
