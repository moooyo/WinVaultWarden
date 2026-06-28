using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

// Send 读编排:拉列表 → 逐条用 UserKey 解出 seed → 派生 cryptoKey → 解密字段 → 映射成领域 Send。
public sealed class SendService : ISendService
{
    private readonly ISendApiClient _api;
    private readonly SendCryptoService _crypto;
    private readonly VaultSession _session;

    public SendService(ISendApiClient api, SendCryptoService crypto, VaultSession session)
    {
        _api = api;
        _crypto = crypto;
        _session = session;
    }

    public async Task<IReadOnlyList<Send>> GetSendsAsync(CancellationToken ct = default)
    {
        var userKey = _session.UserKey ?? throw new InvalidOperationException("Vault is locked.");
        var response = await _api.GetSendsAsync(ct);
        var result = new List<Send>();
        foreach (var dto in response.Data ?? Array.Empty<SendResponseDto>())
            result.Add(Decrypt(dto, userKey));
        return result;
    }

    private Send Decrypt(SendResponseDto dto, SymmetricCryptoKey userKey)
    {
        var seed = _crypto.UnwrapSeed(dto.Key!, userKey);
        var cryptoKey = _crypto.DeriveCryptoKey(seed);

        var type = (SendType)dto.Type;
        var name = _crypto.DecryptField(dto.Name, cryptoKey) ?? string.Empty;
        var notes = _crypto.DecryptField(dto.Notes, cryptoKey);

        SendText? text = null;
        if (dto.Text is not null)
            text = new SendText(_crypto.DecryptField(dto.Text.Text, cryptoKey), dto.Text.Hidden);

        SendFile? file = null;
        if (dto.File is not null)
        {
            var fileName = _crypto.DecryptField(dto.File.FileName, cryptoKey) ?? string.Empty;
            var size = dto.File.Size ?? 0L;
            file = new SendFile(fileName, size, dto.File.SizeName, dto.File.Id);
        }

        var shareUrl = _crypto.BuildShareUrl(_session.Account.ServerUrl, dto.AccessId, seed);

        return new Send
        {
            Id = dto.Id,
            AccessId = dto.AccessId,
            Type = type,
            Name = name,
            Notes = notes,
            Text = text,
            File = file,
            MaxAccessCount = dto.MaxAccessCount,
            AccessCount = dto.AccessCount,
            ExpirationDate = dto.ExpirationDate,
            DeletionDate = dto.DeletionDate,
            Disabled = dto.Disabled,
            HideEmail = dto.HideEmail,
            HasPassword = !string.IsNullOrEmpty(dto.Password),
            ShareUrl = shareUrl,
        };
    }
}
