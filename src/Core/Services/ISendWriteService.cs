using Core.Models;

namespace Core.Services;

public interface ISendWriteService
{
    Task SaveTextSendAsync(SendDraftModel draft, CancellationToken ct = default);

    // Task 12 用 CreateFileSendAsync/UpdateFileSendMetadataAsync/ReplaceFileSendAsync 取代此适配器,
    // 届时必须删除。保留至今只为在 Task 11 内维持跨任务可编译;内部实现已委派给 UpdateFileSendMetadataAsync
    // 复用同一套 seed 解析与三态密码逻辑,不再引入第二套密码语义。
    [Obsolete("Task 12 replaces this adapter with CreateFileSendAsync, UpdateFileSendMetadataAsync, and ReplaceFileSendAsync.")]
    Task SaveFileSendAsync(SendDraftModel draft, byte[] fileBytes, CancellationToken ct = default);

    Task UpdateFileSendMetadataAsync(SendDraftModel draft, CancellationToken ct = default);

    Task DeleteSendAsync(string sendId, CancellationToken ct = default);
    Task<Core.Models.Send> RemovePasswordAsync(string sendId, CancellationToken ct = default);
}
