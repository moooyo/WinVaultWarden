using Api;
using Api.Dtos;

namespace Vault.Tests;

// 手写 IAttachmentApiClient 测试替身。捕获请求、返回预置响应,镜像 FakeSendApiClient 写法。
public sealed class FakeAttachmentApiClient : IAttachmentApiClient
{
    public List<string> Calls { get; } = new();
    public string? LastGetCipherId { get; private set; }
    public string? LastCreateCipherId { get; private set; }
    public AttachmentUploadRequest? LastCreateRequest { get; private set; }
    public string? LastUploadUrl { get; private set; }
    public string? LastUploadFileName { get; private set; }
    public byte[]? LastUploadBuffer { get; private set; }
    public string? LastDeleteCipherId { get; private set; }
    public string? LastDeleteAttachmentId { get; private set; }
    public string? LastDownloadUrl { get; private set; }

    public Func<string, CipherDto>? GetCipherFactory { get; set; }
    public CipherDto GetCipherResult { get; set; } = null!;
    public AttachmentUploadV2Response CreateResult { get; set; } =
        new(AttachmentId: "att-new", Url: "/ciphers/c-1/attachment/att-new", FileUploadType: 0);
    public byte[] DownloadBytes { get; set; } = Array.Empty<byte>();

    public void SetBaseAddress(string baseUrl) { /* no-op in fake */ }

    public Task<CipherDto> GetCipherAsync(string cipherId, CancellationToken ct = default)
    {
        Calls.Add("get-cipher");
        LastGetCipherId = cipherId;
        var dto = GetCipherFactory is not null ? GetCipherFactory(cipherId) : GetCipherResult;
        return Task.FromResult(dto);
    }

    public Task<AttachmentUploadV2Response> CreateAttachmentV2Async(string cipherId, AttachmentUploadRequest request, CancellationToken ct = default)
    {
        Calls.Add("create-v2");
        LastCreateCipherId = cipherId;
        LastCreateRequest = request;
        return Task.FromResult(CreateResult);
    }

    public Task UploadAttachmentDataAsync(string uploadUrl, string encryptedFileName, byte[] encryptedBuffer, CancellationToken ct = default)
    {
        Calls.Add("upload");
        LastUploadUrl = uploadUrl;
        LastUploadFileName = encryptedFileName;
        LastUploadBuffer = encryptedBuffer;
        return Task.CompletedTask;
    }

    public Task DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default)
    {
        Calls.Add("delete");
        LastDeleteCipherId = cipherId;
        LastDeleteAttachmentId = attachmentId;
        return Task.CompletedTask;
    }

    public Task<byte[]> DownloadAttachmentBytesAsync(string downloadUrl, CancellationToken ct = default)
    {
        Calls.Add("download");
        LastDownloadUrl = downloadUrl;
        return Task.FromResult(DownloadBytes);
    }
}
