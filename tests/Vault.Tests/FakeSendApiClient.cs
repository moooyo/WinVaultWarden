using Api;
using Api.Dtos;
using Core.Models;

namespace Vault.Tests;

// 共享的 ISendApiClient 手写测试替身。捕获请求,返回预置响应。Tasks 6/7/8 复用。
public sealed class FakeSendApiClient : ISendApiClient, IPublicSendApiClient
{
    public List<string> Calls { get; } = new();
    public SendRequest? LastCreateText { get; private set; }
    public SendRequest? LastCreateFileV2 { get; private set; }
    public SendRequest? LastUpdate { get; private set; }
    public string? LastUpdateId { get; private set; }
    public string? LastDeleteId { get; private set; }
    public string? LastRemovePasswordId { get; private set; }
    public string? LastUploadUrl { get; private set; }
    public string? LastUploadFileName { get; private set; }
    public byte[]? LastUploadBuffer { get; private set; }

    // Task 12 补偿事务测试注入点:上传/删除失败、单个 sendId 的删除失败、上传失败前的副作用回调,
    // 以及记录清理删除是否用了【未取消】的独立令牌(证明补偿删除不复用被取消的调用方令牌)。
    public Exception? UploadThrows { get; set; }
    public Exception? DeleteThrows { get; set; }
    public Dictionary<string, Exception> DeleteFailures { get; } = [];
    public Action? BeforeUploadFailure { get; set; }
    // 数据完整性竞态测试注入点:模拟"上传成功即将返回前"与"指定 sendId 删除成功即将返回前"lease 失效
    // (回调内通常 session.Lock()),验证 post-upload / post-delete 的 ThrowIfExpired 补偿(补/不补)语义。
    public Action? AfterUploadSuccess { get; set; }
    public Dictionary<string, Action> AfterDeleteSuccess { get; } = [];
    public bool DeleteTokenWasCanceled { get; private set; }
    public string? LastAccessId { get; private set; }
    public string? LastAccessPasswordProof { get; private set; }
    public string? LastDownloadUrl { get; private set; }
    public string? LastAccessFileSendId { get; private set; }
    public string? LastAccessFileFileId { get; private set; }

    private static SendResponseDto EmptyDto() => new(
        Id: "", AccessId: "", Type: 0, Name: "", Notes: null, Text: null, File: null,
        Key: null, MaxAccessCount: null, AccessCount: 0, Password: null, AuthType: 0,
        Disabled: false, HideEmail: false, RevisionDate: null, ExpirationDate: null,
        DeletionDate: DateTimeOffset.UtcNow, Object: null);

    private static SendAccessResponseDto EmptyAccessDto() => new(
        Id: "", Type: 0, Name: "", Text: null, File: null,
        ExpirationDate: null, CreatorIdentifier: null, Object: null);

    private static SendFileUploadV2Response EmptyUploadResponse() => new(
        FileUploadType: 0, Object: null, Url: "", SendResponse: EmptyDto());

    public SendListResponse ListResult { get; set; } = new(Data: Array.Empty<SendResponseDto>(), Object: "list");
    public SendResponseDto CreateTextResult { get; set; } = EmptyDto();
    public SendFileUploadV2Response CreateFileV2Result { get; set; } = EmptyUploadResponse();
    public SendResponseDto UpdateResult { get; set; } = EmptyDto();
    public SendResponseDto RemovePasswordResult { get; set; } = EmptyDto();
    public SendAccessResponseDto AccessResult { get; set; } = EmptyAccessDto();
    public SendFileDownloadResponse AccessFileResult { get; set; } = new(Id: "", Url: "", Object: null);
    public byte[] DownloadBytes { get; set; } = Array.Empty<byte>();
    public Exception? AccessException { get; set; }

    public Task<SendListResponse> GetSendsAsync(CancellationToken ct = default)
    { Calls.Add("list"); return Task.FromResult(ListResult); }

    public Task<SendResponseDto> CreateTextSendAsync(SendRequest request, CancellationToken ct = default)
    { Calls.Add("create-text"); LastCreateText = request; return Task.FromResult(CreateTextResult); }

    public Task<SendFileUploadV2Response> CreateFileSendV2Async(SendRequest request, CancellationToken ct = default)
    { Calls.Add("create-file-v2"); LastCreateFileV2 = request; return Task.FromResult(CreateFileV2Result); }

    // 真正执行 streaming delegate(把密文写进内存缓冲以便断言),失败前先触发 BeforeUploadFailure 副作用。
    public async Task UploadSendFileAsync(string uploadUrl, string encryptedFileName, StreamUploadContent encryptedContent, CancellationToken ct = default)
    {
        Calls.Add("upload");
        LastUploadUrl = uploadUrl;
        LastUploadFileName = encryptedFileName;
        if (UploadThrows is not null)
        {
            BeforeUploadFailure?.Invoke();
            throw UploadThrows;
        }
        await using var output = new MemoryStream();
        await encryptedContent.WriteAsync(output, ct);
        LastUploadBuffer = output.ToArray();
        AfterUploadSuccess?.Invoke();
    }

    public Task<SendResponseDto> UpdateSendAsync(string sendId, SendRequest request, CancellationToken ct = default)
    { Calls.Add("update"); LastUpdateId = sendId; LastUpdate = request; return Task.FromResult(UpdateResult); }

    // 记录令牌是否已取消(补偿删除必须用未取消的独立令牌);按 sendId 精确注入失败,或全局 DeleteThrows。
    public Task DeleteSendAsync(string sendId, CancellationToken ct = default)
    {
        DeleteTokenWasCanceled = ct.IsCancellationRequested;
        ct.ThrowIfCancellationRequested();
        Calls.Add($"delete:{sendId}");
        LastDeleteId = sendId;
        var failure = DeleteFailures.GetValueOrDefault(sendId) ?? DeleteThrows;
        if (failure is not null)
            return Task.FromException(failure);
        AfterDeleteSuccess.GetValueOrDefault(sendId)?.Invoke();
        return Task.CompletedTask;
    }

    public Task<SendResponseDto> RemoveSendPasswordAsync(string sendId, CancellationToken ct = default)
    { Calls.Add("remove-password"); LastRemovePasswordId = sendId; return Task.FromResult(RemovePasswordResult); }

    public Task<SendAccessResponseDto> AccessSendAsync(string accessId, string? passwordProof, CancellationToken ct = default)
    {
        Calls.Add("access"); LastAccessId = accessId; LastAccessPasswordProof = passwordProof;
        if (AccessException is not null) return Task.FromException<SendAccessResponseDto>(AccessException);
        return Task.FromResult(AccessResult);
    }

    public Task<SendFileDownloadResponse> AccessSendFileAsync(string sendId, string fileId, string? passwordProof, CancellationToken ct = default)
    {
        Calls.Add("access-file"); LastAccessFileSendId = sendId; LastAccessFileFileId = fileId;
        return Task.FromResult(AccessFileResult);
    }

    public Task<byte[]> DownloadSendFileBytesAsync(string downloadUrl, CancellationToken ct = default)
    { Calls.Add("download"); LastDownloadUrl = downloadUrl; return Task.FromResult(DownloadBytes); }
}
