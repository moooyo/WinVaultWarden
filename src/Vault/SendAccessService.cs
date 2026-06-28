using Api;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

// Send 访问编排(无需登录):解析分享 URL → 取 accessId+seed → 派生 cryptoKey →
// 如有密码则算证明 → 调访问 API → 解密返回字段。
// 文件下载分两步:1) POST /sends/{sendId}/access/file/{fileId} 换取真实下载 URL;
//                2) GET 下载 URL 取加密字节,再 DecryptBuffer。
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

        // dto.Id は send の UUID;dto.File.Id は file_id。
        // DownloadFileAsync で /sends/{sendId}/access/file/{fileId} を呼ぶために両方を保存する。
        string? sendId = dto.File is not null ? dto.Id : null;
        string? fileId = dto.File?.Id;

        return new SendAccessResult
        {
            Type = type,
            Name = _crypto.DecryptField(dto.Name, cryptoKey) ?? string.Empty,
            TextContent = textContent,
            FileName = fileName,
            FileDownloadUrl = null,   // resolved lazily during DownloadFileAsync
            SendId = sendId,
            FileId = fileId,
            AccessId = accessId,
            Seed = seed,
            PasswordProof = passwordProof,
        };
    }

    public async Task<byte[]> DownloadFileAsync(SendAccessResult accessed, CancellationToken ct = default)
    {
        string downloadUrl;

        // 优先用 sendId+fileId 走两步协议(真实服务端需要中间 POST 换取带 JWT 的下载 URL)。
        if (!string.IsNullOrEmpty(accessed.SendId) && !string.IsNullOrEmpty(accessed.FileId))
        {
            var dlResponse = await _api.AccessSendFileAsync(
                accessed.SendId, accessed.FileId, accessed.PasswordProof, ct);
            downloadUrl = dlResponse.Url;
        }
        else if (!string.IsNullOrEmpty(accessed.FileDownloadUrl))
        {
            // 单测/历史路径:直接提供了下载 URL。
            downloadUrl = accessed.FileDownloadUrl;
        }
        else
        {
            throw new InvalidOperationException("该 Send 没有可下载的文件。");
        }

        var cryptoKey = _crypto.DeriveCryptoKey(accessed.Seed);
        var buffer = await _api.DownloadSendFileBytesAsync(downloadUrl, ct);
        return _crypto.DecryptBuffer(buffer, cryptoKey);
    }
}
