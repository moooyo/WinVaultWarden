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

// Coordinates Send encryption and writes. File uploads use a two-pass, constant-memory stream;
// failed uploads are compensated with an independent deadline and the authorization receipt from
// the successful metadata create. Replacement preserves the new Send whenever the old DELETE has
// an uncertain outcome, preventing loss of both versions.
public sealed class SendWriteService : ISendWriteService
{
    private readonly IVaultApiClientProvider _clients;
    private readonly SendCryptoService _crypto;
    private readonly VaultSession _session;
    private readonly ISendCompensationClient _compensation;
    private readonly ITransferIdleTimeoutPolicy _timeoutPolicy;

    public SendWriteService(
        IVaultApiClientProvider clients,
        SendCryptoService crypto,
        VaultSession session,
        ISendCompensationClient compensation,
        ITransferIdleTimeoutPolicy timeoutPolicy)
    {
        _clients = clients;
        _crypto = crypto;
        _session = session;
        _compensation = compensation;
        _timeoutPolicy = timeoutPolicy ?? throw new ArgumentNullException(nameof(timeoutPolicy));
    }

    public async Task<SendMutationReceipt> SaveTextSendAsync(
        SendDraftModel draft,
        VaultSessionLease expectedSession,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedSession);
        if (!string.IsNullOrEmpty(draft.Id))
            EnsureSupportedExistingAuthentication(draft);
        using var userKey = CopyUserKey(expectedSession);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, expectedSession.CancellationToken);
        var api = _clients.GetAuthenticated<ISendApiClient>(expectedSession);
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
                var created = await api.CreateTextSendAsync(request, linked.Token);
                _session.ThrowIfExpired(expectedSession);
                return CreateReceipt(created, SendMutationKind.Create, expectedSession);
            }
            var updated = await api.UpdateSendAsync(draft.Id, request, linked.Token);
            _session.ThrowIfExpired(expectedSession);
            if (draft.PasswordChange is SendPasswordChange.Remove)
                updated = await api.RemoveSendPasswordAsync(draft.Id, linked.Token);
            _session.ThrowIfExpired(expectedSession);
            return CreateReceipt(updated, SendMutationKind.Update, expectedSession);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    // 创建新文件 Send:先在任何 API 前校验明文上限,再生成新 seed;v2 建记录拿上传 URL → streaming 上传。
    // 上传失败/取消一律用【独立 15s 令牌】补偿删除刚建的孤儿记录(见 CreateFileCoreAsync)。
    public async Task<SendMutationReceipt> CreateFileSendAsync(
        SendDraftModel draft,
        SendFileSource file,
        VaultSessionLease expectedSession,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(draft.Id))
            throw new InvalidOperationException("CreateFileSendAsync requires a new Send draft.");
        SendFileSource.ValidateLength(file.Length);
        ArgumentNullException.ThrowIfNull(expectedSession);
        using var userKey = CopyUserKey(expectedSession);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, expectedSession.CancellationToken);
        var seed = _crypto.GenerateSeed();
        try
        {
            var api = _clients.GetAuthenticated<ISendApiClient>(expectedSession);
            var created = await CreateFileCoreAsync(
                expectedSession,
                api,
                userKey,
                draft,
                file,
                seed,
                SendMutationKind.Create,
                replacedSendId: null,
                ct: linked.Token);
            return created.Receipt;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    // The service cannot replace file bytes in place, so replacement creates and uploads a new
    // Send before deleting the old one. The new Send is rolled back only before DELETE dispatch or
    // after a conclusive non-applied response. Ambiguous transport/server failures preserve it.
    public async Task<SendMutationReceipt> ReplaceFileSendAsync(
        string existingSendId,
        SendDraftModel draft,
        SendFileSource file,
        VaultSessionLease expectedSession,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingSendId);
        if (!string.Equals(draft.Id, existingSendId, StringComparison.Ordinal))
            throw new InvalidOperationException("Replacement draft id must match the existing Send id.");
        SendFileSource.ValidateLength(file.Length);
        EnsureSupportedExistingAuthentication(draft);
        var existingAuthType = EffectiveExistingAuthType(draft);
        if (existingAuthType == SendAuthType.Password
            && draft.PasswordChange is SendPasswordChange.Keep)
            throw new SendPasswordRequiredForReplacementException();

        ArgumentNullException.ThrowIfNull(expectedSession);
        using var userKey = CopyUserKey(expectedSession);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, expectedSession.CancellationToken);
        var seed = ResolveSeed(draft, userKey);
        try
        {
            var api = _clients.GetAuthenticated<ISendApiClient>(expectedSession);
            var created = await CreateFileCoreAsync(
                expectedSession,
                api,
                userKey,
                draft.WithId(null),
                file,
                seed,
                SendMutationKind.Replace,
                existingSendId,
                linked.Token);

            // Before dispatch, cancellation and lease expiry prove the old Send is untouched, so rolling
            // back the new Send is safe. Once the DELETE is dispatched, only a conclusive rejection may
            // trigger rollback; transport failures preserve the replacement.
            try
            {
                _session.ThrowIfExpired(expectedSession);
                linked.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException preDispatchError)
            {
                await DeleteOrThrowCompensationAsync(
                    created.Authorization,
                    created.Receipt.SendId,
                    preDispatchError);
                throw;
            }

            try
            {
                await api.DeleteSendForReplacementAsync(existingSendId, linked.Token);
            }
            catch (OperationCanceledException definitelyNotDispatched)
            {
                // The API contract only exposes a raw cancellation before HttpClient dispatch.
                await DeleteOrThrowCompensationAsync(
                    created.Authorization,
                    created.Receipt.SendId,
                    definitelyNotDispatched);
                throw;
            }
            catch (SendDeleteDefinitelyNotAppliedException rejected)
            {
                await DeleteOrThrowCompensationAsync(
                    created.Authorization,
                    created.Receipt.SendId,
                    rejected);
                throw;
            }
            catch (SendDeleteOutcomeUncertainException uncertain)
            {
                throw new SendReplacementUncertainException(
                    existingSendId,
                    created.Receipt,
                    uncertain);
            }

            _session.ThrowIfExpired(expectedSession);
            return created.Receipt;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    // 文件创建的公共核心(创建/替换共用):任何 API 前再次校验长度,以固定 IV 两遍式常量内存流加密上传;
    // 上传失败/取消用独立令牌补偿删除,清理再失败则抛 SendUploadCompensationException(暴露孤儿 SendId)。
    private async Task<CreatedFileSend> CreateFileCoreAsync(
        VaultSessionLease lease,
        ISendApiClient api,
        SymmetricCryptoKey userKey,
        SendDraftModel draft,
        SendFileSource file,
        byte[] seed,
        SendMutationKind mutationKind,
        string? replacedSendId,
        CancellationToken ct)
    {
        SendFileSource.ValidateLength(file.Length);
        using var cryptoKey = _crypto.DeriveCryptoKey(seed);
        var encryptedName = _crypto.EncryptField(file.FileName, cryptoKey);
        var upload = await _crypto.PrepareFileUploadAsync(
            file,
            cryptoKey,
            _timeoutPolicy,
            ct);
        var request = BuildBaseRequest(draft, seed, cryptoKey, userKey);
        request.Type = (int)SendType.File;
        request.File = new SendFileRequest(encryptedName);
        request.FileLength = checked((int)upload.Length);
        var receipt = await api.CreateFileSendV2Async(request, ct);
        var response = receipt.Value;
        try
        {
            var sendId = response.SendResponse.Id;
            var fileId = response.SendResponse.File?.Id
                ?? throw new InvalidDataException("File Send metadata response is missing its file id.");
            await api.UploadSendFileAsync(
                response.FileUploadType,
                response.Url,
                sendId,
                fileId,
                encryptedName,
                upload,
                ct);
            // 上传成功后、返回前也必须在同一补偿 try 内校验 lease:若此刻 vault 被 Lock,
            // 抛 OCE 前用冻结 binding 删掉刚建的孤儿记录,绝不把新 Send 遗留在服务端。
            _session.ThrowIfExpired(lease);
        }
        catch (Exception uploadError)
        {
            await DeleteOrThrowCompensationAsync(
                receipt.Authorization,
                response.SendResponse.Id,
                uploadError);
            throw;
        }
        return new CreatedFileSend(
            CreateReceipt(
                response.SendResponse,
                mutationKind,
                lease,
                replacedSendId),
            receipt.Authorization);
    }

    // Compensation uses the token binding attached to the successful metadata create, including a
    // token obtained by an automatic 401 refresh. Its deadline is independent from caller/session
    // cancellation so cleanup can finish after a lock or abandoned upload.
    private async Task DeleteOrThrowCompensationAsync(AccessTokenBinding binding, string sendId, Exception originalError)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await _compensation.DeleteAsync(binding, sendId, cleanup.Token);
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
    public async Task<SendMutationReceipt> UpdateFileSendMetadataAsync(
        SendDraftModel draft,
        VaultSessionLease expectedSession,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(draft.Id))
            throw new InvalidOperationException("UpdateFileSendMetadataAsync 只用于更新既有 Send。");
        EnsureSupportedExistingAuthentication(draft);

        ArgumentNullException.ThrowIfNull(expectedSession);
        using var userKey = CopyUserKey(expectedSession);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, expectedSession.CancellationToken);
        var api = _clients.GetAuthenticated<ISendApiClient>(expectedSession);
        var seed = ResolveSeed(draft, userKey);
        try
        {
            using var cryptoKey = _crypto.DeriveCryptoKey(seed);
            var request = BuildBaseRequest(draft, seed, cryptoKey, userKey);
            request.Type = (int)SendType.File;
            // File / FileLength 更新时被服务端忽略(文件不可变),保持 null。

            var updated = await api.UpdateSendAsync(draft.Id, request, linked.Token);
            _session.ThrowIfExpired(expectedSession);
            if (draft.PasswordChange is SendPasswordChange.Remove)
                updated = await api.RemoveSendPasswordAsync(draft.Id, linked.Token);
            _session.ThrowIfExpired(expectedSession);
            return CreateReceipt(updated, SendMutationKind.Update, expectedSession);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public async Task<SendMutationReceipt> DeleteSendAsync(
        string sendId,
        VaultSessionLease expectedSession,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedSession);
        using var userKey = CopyUserKey(expectedSession);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, expectedSession.CancellationToken);
        var api = _clients.GetAuthenticated<ISendApiClient>(expectedSession);
        await api.DeleteSendAsync(sendId, linked.Token);
        _session.ThrowIfExpired(expectedSession);
        return new SendMutationReceipt(
            sendId,
            RevisionDate: null,
            SendMutationKind.Delete,
            SessionLease: expectedSession);
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

    private static SendAuthType EffectiveExistingAuthType(SendDraftModel draft)
    {
        if (draft.ExistingAuthType == SendAuthType.None && draft.ExistingHasPassword)
            return SendAuthType.Unknown;
        return draft.ExistingAuthType;
    }

    private SymmetricCryptoKey CopyUserKey(VaultSessionLease expectedSession)
    {
        if (_session.TryCopyUserKey(expectedSession, out var copiedKey))
            return copiedKey!;

        _session.ThrowIfExpired(expectedSession);
        throw new InvalidOperationException("Vault is locked.");
    }

    private static void EnsureSupportedExistingAuthentication(SendDraftModel draft)
    {
        var existingAuthType = EffectiveExistingAuthType(draft);
        if (existingAuthType is SendAuthType.Email or SendAuthType.Unknown)
        {
            throw new SendUnsupportedAuthenticationException(
                existingAuthType,
                draft.ExistingRawAuthType);
        }
    }

    private static SendMutationReceipt CreateReceipt(
        SendResponseDto response,
        SendMutationKind kind,
        VaultSessionLease lease,
        string? replacedSendId = null) =>
        new(
            response.Id,
            response.RevisionDate,
            kind,
            response.AccessId,
            SendMutationWitness.Compute(response),
            lease,
            replacedSendId);

    private sealed record CreatedFileSend(
        SendMutationReceipt Receipt,
        AccessTokenBinding Authorization);

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
