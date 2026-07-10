using Core.Models;
using Core.Session;

namespace Core.Services;

// 个人密码库写操作编排:加密 → 调用写 API → 整库重新同步。
// 所有方法要求 vault 处于解锁态,否则抛 InvalidOperationException。
public interface IVaultWriteService
{
    // cipher.Id 为空 → 新建;否则 → 更新。
    Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default);

    // lease-bound 重载:复用请求入口捕获的同一 lease,跨审批/网络 await 边界后仍以原 lease
    // 校验会话未被 Lock/切换账户,防止陈旧请求把凭据写入或同步到别的账户。默认实现委托无 lease
    // 重载(供不关心 lease 的测试替身复用);真实 VaultWriteService 覆盖为按传入 lease 严格门禁。
    Task SaveCipherAsync(Cipher cipher, VaultSessionLease lease, CancellationToken ct = default) =>
        SaveCipherAsync(cipher, ct);

    // permanent=false → 移入回收站(软删除);true → 永久删除。
    Task DeleteCipherAsync(string cipherId, bool permanent, CancellationToken ct = default);

    Task RestoreCipherAsync(string cipherId, CancellationToken ct = default);

    // folderId 为 null/空 → 新建文件夹;否则 → 重命名。
    Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default);

    Task DeleteFolderAsync(string folderId, CancellationToken ct = default);

    // 批量:ids 为空 → 直接返回(不发请求)。folderId null/空 → 移到根。
    Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default);
    // permanent=true → 批量硬删;false → 批量软删(移入回收站)。
    Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default);
    Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default);
}
