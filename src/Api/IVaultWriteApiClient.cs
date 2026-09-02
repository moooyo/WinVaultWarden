using Api.Dtos;

namespace Api;

// 个人密码库写操作。每个方法对应一个 Vaultwarden 端点;
// 4xx 失败统一抛 VaultWriteException(message 取自服务端错误体)。
public interface IVaultWriteApiClient : IAuthenticatedApiRole
{
    Task CreateCipherAsync(CipherRequest request, CancellationToken ct = default);
    async Task<CipherDto?> CreateCipherWithResponseAsync(
        CipherRequest request,
        CancellationToken ct = default)
    {
        await CreateCipherAsync(request, ct);
        return null;
    }
    Task CreateOrganizationCipherAsync(ShareCipherRequest request, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Organization cipher creation is not supported by this API client."));
    async Task<CipherDto?> CreateOrganizationCipherWithResponseAsync(
        ShareCipherRequest request,
        CancellationToken ct = default)
    {
        await CreateOrganizationCipherAsync(request, ct);
        return null;
    }
    Task UpdateCipherAsync(string cipherId, CipherRequest request, CancellationToken ct = default);
    Task ShareExistingCipherAsync(
        string cipherId,
        ShareCipherRequest request,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Cipher sharing is not supported by this API client."));
    Task BulkShareCiphersAsync(
        BulkShareCiphersRequest request,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Bulk cipher sharing is not supported by this API client."));
    Task SetCipherCollectionsAsync(
        string cipherId,
        CipherCollectionsRequest request,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Cipher collection assignment is not supported by this API client."));
    Task BulkUpdateCipherCollectionsAsync(
        BulkCipherCollectionsRequest request,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Bulk cipher collection updates are not supported by this API client."));
    Task UpdateCipherPartialAsync(
        string cipherId,
        PartialCipherRequest request,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Partial cipher updates are not supported by this API client."));
    Task SoftDeleteCipherAsync(string cipherId, CancellationToken ct = default);
    Task HardDeleteCipherAsync(string cipherId, CancellationToken ct = default);
    Task RestoreCipherAsync(string cipherId, CancellationToken ct = default);
    Task ArchiveCipherAsync(string cipherId, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Cipher archiving is not supported by this API client."));
    Task UnarchiveCipherAsync(string cipherId, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Cipher unarchiving is not supported by this API client."));
    Task BulkSoftDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default);
    Task BulkHardDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default);
    Task BulkRestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default);
    Task BulkArchiveCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Bulk cipher archiving is not supported by this API client."));
    Task BulkUnarchiveCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Bulk cipher unarchiving is not supported by this API client."));
    Task BulkMoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default);
    Task CreateFolderAsync(FolderRequest request, CancellationToken ct = default);
    Task UpdateFolderAsync(string folderId, FolderRequest request, CancellationToken ct = default);
    Task DeleteFolderAsync(string folderId, CancellationToken ct = default);
    Task ImportCiphersAsync(ImportRequest request, CancellationToken ct = default);
}
