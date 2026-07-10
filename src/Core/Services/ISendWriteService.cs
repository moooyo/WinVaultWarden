using Core.Models;

namespace Core.Services;

public interface ISendWriteService
{
    Task SaveTextSendAsync(SendDraftModel draft, CancellationToken ct = default);

    // 文件型 Send 的三个显式入口(Task 12 取代旧 byte[] 适配器):
    //   - CreateFileSendAsync:新建 → v2 建记录 → streaming 上传;失败/取消用独立令牌补偿删除。
    //   - UpdateFileSendMetadataAsync:只改元数据(名称/密码三态/到期等),绝不重传文件字节。
    //   - ReplaceFileSendAsync:建+传新件后删旧件;删旧失败则回滚删新,任何失败不伪成功。
    Task<SendFileWriteResult> CreateFileSendAsync(SendDraftModel draft, SendFileSource file, CancellationToken ct = default);

    Task UpdateFileSendMetadataAsync(SendDraftModel draft, CancellationToken ct = default);

    Task<SendFileWriteResult> ReplaceFileSendAsync(string existingSendId, SendDraftModel draft, SendFileSource file, CancellationToken ct = default);

    Task DeleteSendAsync(string sendId, CancellationToken ct = default);
}
