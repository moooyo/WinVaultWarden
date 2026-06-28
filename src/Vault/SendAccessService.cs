using Api;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

// Send 访问编排(无需登录):解析分享 URL → 取 accessId+seed → 派生 cryptoKey →
// 如有密码则算证明 → 调访问 API → 解密返回字段。文件下载单独 GET 后解密 EncArrayBuffer。
public sealed class SendAccessService : ISendAccessService
{
    private readonly ISendApiClient _api;
    private readonly SendCryptoService _crypto;

    public SendAccessService(ISendApiClient api, SendCryptoService crypto)
    {
        _api = api;
        _crypto = crypto;
    }

    public async Task<SendAccessResult> AccessAsync(string shareUrl, string? password, CancellationToken ct = default)
    {
        if (!_crypto.TryParseShareUrl(shareUrl, out var accessId, out var seed))
            throw new FormatException("无法解析 Send 分享 URL。");

        var cryptoKey = _crypto.DeriveCryptoKey(seed);
        string? passwordProof = string.IsNullOrEmpty(password)
            ? null
            : _crypto.ComputePasswordProof(password, seed);

        var dto = await _api.AccessSendAsync(accessId, passwordProof, ct);

        var type = (SendType)dto.Type;
        string? textContent = dto.Text is null ? null : _crypto.DecryptField(dto.Text.Text, cryptoKey);
        string? fileName = dto.File is null ? null : _crypto.DecryptField(dto.File.FileName, cryptoKey);
        string? fileDownloadUrl = dto.File is null ? null : dto.File.Id;

        return new SendAccessResult
        {
            Type = type,
            Name = _crypto.DecryptField(dto.Name, cryptoKey) ?? string.Empty,
            TextContent = textContent,
            FileName = fileName,
            FileDownloadUrl = fileDownloadUrl,
            AccessId = accessId,
            Seed = seed,
        };
    }

    public async Task<byte[]> DownloadFileAsync(SendAccessResult accessed, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(accessed.FileDownloadUrl))
            throw new InvalidOperationException("该 Send 没有可下载的文件。");

        var cryptoKey = _crypto.DeriveCryptoKey(accessed.Seed);
        var buffer = await _api.DownloadSendFileBytesAsync(accessed.FileDownloadUrl, ct);
        return _crypto.DecryptBuffer(buffer, cryptoKey);
    }
}
