using Core.Models;

namespace Core.Services;

// 个人密码库写操作编排:加密 → 调用写 API → 整库重新同步。
// 所有方法要求 vault 处于解锁态,否则抛 InvalidOperationException。
public interface IVaultWriteService
{
    // cipher.Id 为空 → 新建;否则 → 更新。
    Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default);

    // permanent=false → 移入回收站(软删除);true → 永久删除。
    Task DeleteCipherAsync(string cipherId, bool permanent, CancellationToken ct = default);

    Task RestoreCipherAsync(string cipherId, CancellationToken ct = default);

    // folderId 为 null/空 → 新建文件夹;否则 → 重命名。
    Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default);

    Task DeleteFolderAsync(string folderId, CancellationToken ct = default);
}
