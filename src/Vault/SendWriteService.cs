using System.Globalization;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

// Send 写编排:生成 seed → 派生 cryptoKey → 加密字段 → 用 UserKey 包裹 seed → 组 SendRequest → 创建/更新。
// 文件类型:把文件加密成 EncArrayBuffer,fileLength=buffer.Length,先 v2 创建拿上传 URL,再 multipart 上传。
public sealed class SendWriteService : ISendWriteService
{
    private readonly ISendApiClient _api;
    private readonly SendCryptoService _crypto;
    private readonly VaultSession _session;

    public SendWriteService(ISendApiClient api, SendCryptoService crypto, VaultSession session)
    {
        _api = api;
        _crypto = crypto;
        _session = session;
    }

    public async Task SaveTextSendAsync(SendDraftModel draft, CancellationToken ct = default)
    {
        var userKey = RequireUserKey();
        var seed = _crypto.GenerateSeed();
        var cryptoKey = _crypto.DeriveCryptoKey(seed);

        var request = BuildBaseRequest(draft, seed, cryptoKey, userKey);
        request.Type = (int)SendType.Text;
        request.Text = new SendTextRequest(
            Text: draft.TextContent is null ? null : _crypto.EncryptField(draft.TextContent, cryptoKey),
            Hidden: draft.TextHidden);

        if (string.IsNullOrEmpty(draft.Id))
            await _api.CreateTextSendAsync(request, ct);
        else
            await _api.UpdateSendAsync(draft.Id, request, ct);
    }

    public async Task SaveFileSendAsync(SendDraftModel draft, byte[] fileBytes, CancellationToken ct = default)
    {
        var userKey = RequireUserKey();
        var seed = _crypto.GenerateSeed();
        var cryptoKey = _crypto.DeriveCryptoKey(seed);

        var buffer = _crypto.EncryptToBuffer(fileBytes, cryptoKey);
        var encryptedFileName = _crypto.EncryptField(draft.FileName ?? string.Empty, cryptoKey);

        var request = BuildBaseRequest(draft, seed, cryptoKey, userKey);
        request.Type = (int)SendType.File;
        request.File = new SendFileRequest(FileName: encryptedFileName);
        request.FileLength = buffer.Length;

        var response = await _api.CreateFileSendV2Async(request, ct);
        await _api.UploadSendFileAsync(response.Url, encryptedFileName, buffer, ct);
    }

    public Task DeleteSendAsync(string sendId, CancellationToken ct = default)
    {
        RequireUserKey();
        return _api.DeleteSendAsync(sendId, ct);
    }

    public async Task<Send> RemovePasswordAsync(string sendId, CancellationToken ct = default)
    {
        var userKey = RequireUserKey();
        var dto = await _api.RemoveSendPasswordAsync(sendId, ct);
        return Decrypt(dto, userKey);
    }

    // 组装通用字段(name/notes/key/password 证明/到期/删除日期/标志位)。
    // 返回可变 SendRequest 以便调用方补填 Type/Text/File/FileLength。
    private MutableSendRequest BuildBaseRequest(
        SendDraftModel draft, byte[] seed, SymmetricCryptoKey cryptoKey, SymmetricCryptoKey userKey)
    {
        string? passwordProof = string.IsNullOrEmpty(draft.Password)
            ? null
            : _crypto.ComputePasswordProof(draft.Password, seed);

        return new MutableSendRequest
        {
            Id = string.IsNullOrEmpty(draft.Id) ? null : draft.Id,
            Name = _crypto.EncryptField(draft.Name, cryptoKey),
            Notes = draft.Notes is null ? null : _crypto.EncryptField(draft.Notes, cryptoKey),
            Key = _crypto.WrapSeed(seed, userKey),
            Password = passwordProof,
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
