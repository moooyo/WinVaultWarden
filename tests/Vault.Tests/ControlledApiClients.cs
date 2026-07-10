using Api;
using Api.Dtos;

namespace Vault.Tests;

public sealed class ControlledReadonlyApiClient(
    Func<CancellationToken, Task<SyncResponse>> getSync) : IReadonlyApiClient
{
    public Func<CancellationToken, Task<ListResponse<DeviceDto>>> GetDevices { get; init; } =
        _ => throw new NotSupportedException();
    public Func<string, CancellationToken, Task<FolderDto>> GetFolder { get; init; } =
        (_, _) => throw new NotSupportedException();

    public Task<SyncResponse> GetSyncAsync(CancellationToken ct = default) =>
        getSync(ct);
    public Task<ListResponse<DeviceDto>> GetDevicesAsync(CancellationToken ct = default) =>
        GetDevices(ct);
    public Task<FolderDto> GetFolderAsync(string folderId, CancellationToken ct = default) =>
        GetFolder(folderId, ct);
    public Task<SendResponseDto> GetSendAsync(string sendId, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

public sealed class ControlledIdentityApiClient(
    Func<CancellationToken, Task<ConnectTokenResult>> connect) : IIdentityApiClient
{
    public int ConnectCallCount { get; private set; }
    public PreloginResponse PreloginResult { get; init; } =
        new(Core.Enums.KdfType.Pbkdf2, 600_000, null, null);

    public Task<ConfigResponse> GetConfigAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<PreloginResponse> PreloginAsync(
        string email, CancellationToken ct = default) =>
        Task.FromResult(PreloginResult);
    public Task<ConnectTokenResult> ConnectTokenAsync(
        ConnectTokenRequest request, CancellationToken ct = default)
    {
        ConnectCallCount++;
        return connect(ct);
    }
    public Task SendTwoFactorEmailLoginAsync(
        string email,
        string masterPasswordHash,
        string deviceIdentifier,
        CancellationToken ct = default) =>
        Task.CompletedTask;
}

public sealed class BlockingAccountApiClient(
    Func<CancellationToken, Task> changePassword) : IAccountApiClient
{
    public Task UpdateProfileAsync(
        ProfileUpdateRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task ChangePasswordAsync(
        ChangePasswordRequest request, CancellationToken ct = default) =>
        changePassword(ct);
    public Task ChangeKdfAsync(
        ChangeKdfRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

public sealed class BlockingAttachmentApiClient(
    Func<CancellationToken, Task<CipherDto>> getCipher) : IAttachmentApiClient
{
    public Task<CipherDto> GetCipherAsync(
        string cipherId, CancellationToken ct = default) =>
        getCipher(ct);
    public Task<AttachmentUploadV2Response> CreateAttachmentV2Async(
        string cipherId,
        AttachmentUploadRequest request,
        CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task UploadAttachmentDataAsync(
        string uploadUrl,
        string encryptedFileName,
        byte[] encryptedBuffer,
        CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task DeleteAttachmentAsync(
        string cipherId,
        string attachmentId,
        CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<byte[]> DownloadAttachmentBytesAsync(
        string downloadUrl,
        CancellationToken ct = default) =>
        throw new NotSupportedException();
}
