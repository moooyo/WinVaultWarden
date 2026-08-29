using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Session;

namespace Vault.Tests;

// 共享的 ISendApiClient 手写测试替身。捕获请求,返回预置响应。Tasks 6/7/8 复用。
public sealed class FakeSendApiClient : ISendApiClient, IPublicSendApiClient
{
    private readonly object _callsGate = new();
    private int _accessTokenRequestCount;

    public List<string> Calls { get; } = new();
    public SendRequest? LastCreateText { get; private set; }
    public SendRequest? LastCreateFileV2 { get; private set; }
    public SendRequest? LastUpdate { get; private set; }
    public string? LastUpdateId { get; private set; }
    public string? LastDeleteId { get; private set; }
    public string? LastRemovePasswordId { get; private set; }
    public string? LastUploadUrl { get; private set; }
    public FileUploadType? LastUploadType { get; private set; }
    public string? LastUploadSendId { get; private set; }
    public string? LastUploadFileId { get; private set; }
    public string? LastUploadFileName { get; private set; }
    public byte[]? LastUploadBuffer { get; private set; }
    public StreamUploadContent? LastUploadContent { get; private set; }

    // Task 12 补偿事务测试注入点:上传/删除失败、单个 sendId 的删除失败、上传失败前的副作用回调,
    // 以及记录清理删除是否用了【未取消】的独立令牌(证明补偿删除不复用被取消的调用方令牌)。
    public Exception? UploadThrows { get; set; }
    public Func<StreamUploadContent, CancellationToken, Task>? UploadAction { get; set; }
    public Exception? DeleteThrows { get; set; }
    public Dictionary<string, Exception> DeleteFailures { get; } = [];
    public Action? BeforeUploadFailure { get; set; }
    // 数据完整性竞态测试注入点:模拟"上传成功即将返回前"与"指定 sendId 删除成功即将返回前"lease 失效
    // (回调内通常 session.Lock()),验证 post-upload / post-delete 的 ThrowIfExpired 补偿(补/不补)语义。
    public Action? AfterUploadSuccess { get; set; }
    public Dictionary<string, Action> AfterDeleteSuccess { get; } = [];
    // 确定性竞态测试注入点:非 null 时,upload 记录调用后在此 Task 上等待(不 ConfigureAwait,以捕获调用方
    // 的 SynchronizationContext)。让测试能用手动 SynchronizationContext 精确 pump upload 之后的 continuation,
    // 在"inner CreateFileCore 完成"与"outer 删旧前 lease 校验"之间插入 Lock 以复现租约竞态。
    public Task? UploadGate { get; set; }
    public bool DeleteTokenWasCanceled { get; private set; }
    public string? LastAccessId { get; private set; }
    public string? LastAccessPasswordProof { get; private set; }
    public string? LastAccessToken { get; private set; }
    public string? LastDownloadUrl { get; private set; }
    public string? LastAccessFileSendId { get; private set; }
    public string? LastAccessFileFileId { get; private set; }
    public Action? AfterAccessFileStarted { get; set; }
    public Task? AccessFileGate { get; set; }
    public Action? AfterAccessFileWithTokenStarted { get; set; }
    public Task? AccessFileWithTokenGate { get; set; }

    private static SendResponseDto EmptyDto() => new(
        Id: "", AccessId: "", Type: 0, Name: "", Notes: null, Text: null, File: null,
        Key: null, MaxAccessCount: null, AccessCount: 0, Password: null, AuthType: 2,
        Disabled: false, HideEmail: false, RevisionDate: null, ExpirationDate: null,
        DeletionDate: DateTimeOffset.UtcNow, Object: null);

    private static SendAccessResponseDto EmptyAccessDto() => new(
        Id: "", Type: 0, Name: "", Text: null, File: null,
        ExpirationDate: null, CreatorIdentifier: null, Object: null);

    private static SendFileUploadV2Response EmptyUploadResponse() => new(
        FileUploadType: FileUploadType.Direct,
        Object: null,
        Url: "",
        SendResponse: EmptyDto() with
        {
            File = new SendFileDto("file-new", string.Empty, null, null),
        });

    public SendListResponse ListResult { get; set; } = new(Data: Array.Empty<SendResponseDto>(), Object: "list");
    public SendResponseDto CreateTextResult { get; set; } = EmptyDto();
    public SendFileUploadV2Response CreateFileV2Result { get; set; } = EmptyUploadResponse();
    public AccessTokenBinding CreateFileV2Authorization { get; set; } = new(
        "test-account",
        new ServerOrigin("https://vault.example"),
        1,
        "test-access-token");
    public SendResponseDto UpdateResult { get; set; } = EmptyDto();
    public SendResponseDto RemovePasswordResult { get; set; } = EmptyDto();
    public SendAccessResponseDto AccessResult { get; set; } = EmptyAccessDto();
    public SendAccessTokenResult AccessTokenResult { get; set; } =
        new SendAccessTokenResult.Unsupported();
    public Func<int, CancellationToken, Task<SendAccessTokenResult>>? AccessTokenAction { get; set; }
    public int AccessTokenRequestCount => Volatile.Read(ref _accessTokenRequestCount);
    public SendFileDownloadResponse AccessFileResult { get; set; } = new(Id: "", Url: "", Object: null);
    public byte[] DownloadBytes { get; set; } = Array.Empty<byte>();
    public Exception? AccessException { get; set; }

    public Task<SendListResponse> GetSendsAsync(CancellationToken ct = default)
    { RecordCall("list"); return Task.FromResult(ListResult); }

    public Task<SendResponseDto> CreateTextSendAsync(SendRequest request, CancellationToken ct = default)
    { RecordCall("create-text"); LastCreateText = request; return Task.FromResult(CreateTextResult); }

    public Task<AuthenticatedWriteReceipt<SendFileUploadV2Response>> CreateFileSendV2Async(
        SendRequest request,
        CancellationToken ct = default)
    {
        RecordCall("create-file-v2");
        LastCreateFileV2 = request;
        return Task.FromResult(new AuthenticatedWriteReceipt<SendFileUploadV2Response>(
            CreateFileV2Result,
            CreateFileV2Authorization));
    }

    // 真正执行 streaming delegate(把密文写进内存缓冲以便断言),失败前先触发 BeforeUploadFailure 副作用。
    public async Task UploadSendFileAsync(
        FileUploadType fileUploadType,
        string uploadUrl,
        string sendId,
        string fileId,
        string encryptedFileName,
        StreamUploadContent encryptedContent,
        CancellationToken ct = default)
    {
        RecordCall("upload");
        LastUploadType = fileUploadType;
        LastUploadUrl = uploadUrl;
        LastUploadSendId = sendId;
        LastUploadFileId = fileId;
        LastUploadFileName = encryptedFileName;
        LastUploadContent = encryptedContent;
        if (!Enum.IsDefined(fileUploadType))
            throw new InvalidDataException($"Unsupported file upload type: {(int)fileUploadType}.");
        if (UploadGate is not null)
            await UploadGate;
        if (UploadThrows is not null)
        {
            BeforeUploadFailure?.Invoke();
            throw UploadThrows;
        }
        if (UploadAction is not null)
        {
            await UploadAction(encryptedContent, ct);
            return;
        }
        await using var output = new MemoryStream();
        await encryptedContent.WriteAsync(output, ct);
        LastUploadBuffer = output.ToArray();
        AfterUploadSuccess?.Invoke();
    }

    public Task<SendResponseDto> UpdateSendAsync(string sendId, SendRequest request, CancellationToken ct = default)
    {
        RecordCall("update");
        LastUpdateId = sendId;
        LastUpdate = request;
        return Task.FromResult(string.IsNullOrEmpty(UpdateResult.Id)
            ? UpdateResult with { Id = sendId }
            : UpdateResult);
    }

    // 记录令牌是否已取消(补偿删除必须用未取消的独立令牌);按 sendId 精确注入失败,或全局 DeleteThrows。
    public Task DeleteSendAsync(string sendId, CancellationToken ct = default)
        => DeleteCore(sendId, ct);

    public Task DeleteSendForReplacementAsync(string sendId, CancellationToken ct = default)
        => DeleteCore(sendId, ct);

    private Task DeleteCore(string sendId, CancellationToken ct)
    {
        DeleteTokenWasCanceled = ct.IsCancellationRequested;
        ct.ThrowIfCancellationRequested();
        RecordCall($"delete:{sendId}");
        LastDeleteId = sendId;
        var failure = DeleteFailures.GetValueOrDefault(sendId) ?? DeleteThrows;
        if (failure is not null)
            return Task.FromException(failure);
        AfterDeleteSuccess.GetValueOrDefault(sendId)?.Invoke();
        return Task.CompletedTask;
    }

    public Task<SendResponseDto> RemoveSendPasswordAsync(string sendId, CancellationToken ct = default)
    { RecordCall("remove-password"); LastRemovePasswordId = sendId; return Task.FromResult(RemovePasswordResult); }

    public Task<SendAccessResponseDto> AccessSendAsync(string accessId, string? passwordProof, CancellationToken ct = default)
    {
        RecordCall("access"); LastAccessId = accessId; LastAccessPasswordProof = passwordProof;
        if (AccessException is not null) return Task.FromException<SendAccessResponseDto>(AccessException);
        return Task.FromResult(AccessResult);
    }

    public async Task<SendAccessTokenResult> RequestSendAccessTokenAsync(
        string accessId,
        string? passwordProof,
        CancellationToken ct = default)
    {
        RecordCall("request-access-token");
        LastAccessId = accessId;
        LastAccessPasswordProof = passwordProof;
        var requestNumber = Interlocked.Increment(ref _accessTokenRequestCount);
        return AccessTokenAction is null
            ? AccessTokenResult
            : await AccessTokenAction(requestNumber, ct);
    }

    public Task<SendAccessResponseDto> AccessSendWithTokenAsync(
        string accessToken,
        CancellationToken ct = default)
    {
        RecordCall("access-with-token");
        LastAccessToken = accessToken;
        return Task.FromResult(AccessResult);
    }

    public async Task<SendFileDownloadResponse> AccessSendFileWithTokenAsync(
        string fileId,
        string accessToken,
        CancellationToken ct = default)
    {
        RecordCall("access-file-with-token");
        LastAccessFileFileId = fileId;
        LastAccessToken = accessToken;
        AfterAccessFileWithTokenStarted?.Invoke();
        if (AccessFileWithTokenGate is not null)
            await AccessFileWithTokenGate.WaitAsync(ct);
        return AccessFileResult;
    }

    public async Task<SendFileDownloadResponse> AccessSendFileAsync(string sendId, string fileId, string? passwordProof, CancellationToken ct = default)
    {
        RecordCall("access-file"); LastAccessFileSendId = sendId; LastAccessFileFileId = fileId;
        AfterAccessFileStarted?.Invoke();
        if (AccessFileGate is not null)
            await AccessFileGate.WaitAsync(ct);
        return AccessFileResult;
    }

    public Task<DownloadContent> OpenSendFileDownloadAsync(
        string downloadUrl,
        CancellationToken ct = default)
    {
        RecordCall("download");
        LastDownloadUrl = downloadUrl;
        return Task.FromResult(new DownloadContent(
            new MemoryStream(DownloadBytes, writable: false),
            DownloadBytes.LongLength));
    }

    private void RecordCall(string call)
    {
        lock (_callsGate)
            Calls.Add(call);
    }
}
