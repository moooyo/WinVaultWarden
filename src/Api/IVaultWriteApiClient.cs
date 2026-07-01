using Api.Dtos;

namespace Api;

// 个人密码库写操作。每个方法对应一个 Vaultwarden 端点;
// 4xx 失败统一抛 VaultWriteException(message 取自服务端错误体)。
public interface IVaultWriteApiClient
{
    void SetBaseAddress(string baseUrl);
    Task CreateCipherAsync(CipherRequest request, CancellationToken ct = default);
    Task UpdateCipherAsync(string cipherId, CipherRequest request, CancellationToken ct = default);
    Task SoftDeleteCipherAsync(string cipherId, CancellationToken ct = default);
    Task HardDeleteCipherAsync(string cipherId, CancellationToken ct = default);
    Task RestoreCipherAsync(string cipherId, CancellationToken ct = default);
    Task BulkSoftDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default);
    Task BulkHardDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default);
    Task BulkRestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default);
    Task BulkMoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default);
    Task CreateFolderAsync(FolderRequest request, CancellationToken ct = default);
    Task UpdateFolderAsync(string folderId, FolderRequest request, CancellationToken ct = default);
    Task DeleteFolderAsync(string folderId, CancellationToken ct = default);
    Task ImportCiphersAsync(ImportRequest request, CancellationToken ct = default);
}
