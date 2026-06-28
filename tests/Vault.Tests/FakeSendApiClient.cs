using Api;
using Api.Dtos;

namespace Vault.Tests;

// 共享的 ISendApiClient 手写测试替身。捕获请求,返回预置响应。Tasks 6/7/8 复用。
public sealed class FakeSendApiClient : ISendApiClient
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

    public void SetBaseAddress(string baseUrl) { /* no-op in fake */ }

    public Task<SendListResponse> GetSendsAsync(CancellationToken ct = default)
    { Calls.Add("list"); return Task.FromResult(ListResult); }

    public Task<SendResponseDto> CreateTextSendAsync(SendRequest request, CancellationToken ct = default)
    { Calls.Add("create-text"); LastCreateText = request; return Task.FromResult(CreateTextResult); }

    public Task<SendFileUploadV2Response> CreateFileSendV2Async(SendRequest request, CancellationToken ct = default)
    { Calls.Add("create-file-v2"); LastCreateFileV2 = request; return Task.FromResult(CreateFileV2Result); }

    public Task UploadSendFileAsync(string uploadUrl, string encryptedFileName, byte[] encryptedBuffer, CancellationToken ct = default)
    { Calls.Add("upload"); LastUploadUrl = uploadUrl; LastUploadFileName = encryptedFileName; LastUploadBuffer = encryptedBuffer; return Task.CompletedTask; }

    public Task<SendResponseDto> UpdateSendAsync(string sendId, SendRequest request, CancellationToken ct = default)
    { Calls.Add("update"); LastUpdateId = sendId; LastUpdate = request; return Task.FromResult(UpdateResult); }

    public Task DeleteSendAsync(string sendId, CancellationToken ct = default)
    { Calls.Add("delete"); LastDeleteId = sendId; return Task.CompletedTask; }

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
