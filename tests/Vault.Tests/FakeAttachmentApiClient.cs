using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Session;

namespace Vault.Tests;

// Captures attachment requests and returns configured responses.
public sealed class FakeAttachmentApiClient : IAttachmentApiClient
{
    public List<string> Calls { get; } = new();
    public string? LastGetCipherId { get; private set; }
    public string? LastCreateCipherId { get; private set; }
    public AttachmentUploadRequest? LastCreateRequest { get; private set; }
    public string? LastUploadUrl { get; private set; }
    public FileUploadType? LastUploadType { get; private set; }
    public string? LastUploadCipherId { get; private set; }
    public string? LastUploadAttachmentId { get; private set; }
    public string? LastUploadFileName { get; private set; }
    public byte[]? LastUploadBuffer { get; private set; }
    public string? LastDeleteCipherId { get; private set; }
    public string? LastDeleteAttachmentId { get; private set; }
    public string? LastDownloadUrl { get; private set; }

    public Func<string, CipherDto>? GetCipherFactory { get; set; }
    public Action<string>? AfterGetCipher { get; set; }
    public CipherDto GetCipherResult { get; set; } = null!;
    public AttachmentUploadV2Response CreateResult { get; set; } =
        new(
            AttachmentId: "att-new",
            Url: "/ciphers/c-1/attachment/att-new",
            FileUploadType: FileUploadType.Direct);
    public AccessTokenBinding CreateAuthorization { get; set; } = new(
        "test-account",
        new ServerOrigin("https://vault.example"),
        1,
        "test-access-token");
    public byte[] DownloadBytes { get; set; } = Array.Empty<byte>();
    public Exception? CreateThrows { get; set; }

    // Injects upload and cleanup failures to verify orphan compensation.
    public Exception? UploadThrows { get; set; }
    public Exception? DeleteThrows { get; set; }
    public Action? AfterUploadSuccess { get; set; }

    public Task<CipherDto> GetCipherAsync(string cipherId, CancellationToken ct = default)
    {
        Calls.Add("get-cipher");
        LastGetCipherId = cipherId;
        var dto = GetCipherFactory is not null ? GetCipherFactory(cipherId) : GetCipherResult;
        AfterGetCipher?.Invoke(cipherId);
        return Task.FromResult(dto);
    }

    public Task<AuthenticatedWriteReceipt<AttachmentUploadV2Response>> CreateAttachmentV2Async(
        string cipherId,
        AttachmentUploadRequest request,
        CancellationToken ct = default)
    {
        Calls.Add("create-v2");
        LastCreateCipherId = cipherId;
        LastCreateRequest = request;
        if (CreateThrows is not null)
            throw CreateThrows;
        return Task.FromResult(new AuthenticatedWriteReceipt<AttachmentUploadV2Response>(
            CreateResult,
            CreateAuthorization));
    }

    public async Task UploadAttachmentDataAsync(
        FileUploadType fileUploadType,
        string uploadUrl,
        string cipherId,
        string attachmentId,
        string encryptedFileName,
        StreamUploadContent encryptedContent,
        CancellationToken ct = default)
    {
        Calls.Add("upload");
        LastUploadType = fileUploadType;
        LastUploadUrl = uploadUrl;
        LastUploadCipherId = cipherId;
        LastUploadAttachmentId = attachmentId;
        LastUploadFileName = encryptedFileName;
        if (!Enum.IsDefined(fileUploadType))
            throw new InvalidDataException($"Unsupported file upload type: {(int)fileUploadType}.");
        if (UploadThrows is not null)
            throw UploadThrows;
        await using var output = new MemoryStream();
        await encryptedContent.WriteAsync(output, ct);
        LastUploadBuffer = output.ToArray();
        AfterUploadSuccess?.Invoke();
    }

    public Task DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default)
    {
        // Compensation must use a token independent from the cancelled upload operation.
        ct.ThrowIfCancellationRequested();
        Calls.Add("delete");
        LastDeleteCipherId = cipherId;
        LastDeleteAttachmentId = attachmentId;
        return DeleteThrows is not null ? Task.FromException(DeleteThrows) : Task.CompletedTask;
    }

    public Task<DownloadContent> OpenAttachmentDownloadAsync(
        string downloadUrl,
        CancellationToken ct = default)
    {
        Calls.Add("download");
        LastDownloadUrl = downloadUrl;
        return Task.FromResult(new DownloadContent(
            new MemoryStream(DownloadBytes, writable: false),
            DownloadBytes.LongLength));
    }
}
