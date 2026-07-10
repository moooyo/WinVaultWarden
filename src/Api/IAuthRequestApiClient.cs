using Api.Dtos;

namespace Api;

// auth-request 端点。Vaultwarden: src/api/core/accounts.rs
// 三种场景：(1) 发起方 POST 创建 request；(2) 审批方 GET pending 列表 + PUT 批准/拒绝；
// (3) 发起方轮询 GET response 等待审批结果。
public interface IAuthRequestApiClient : IAuthenticatedApiRole
{
    // GET api/auth-requests/pending — 审批方拉取所有待处理的请求列表。
    Task<AuthRequestListResponse> GetPendingAsync(CancellationToken ct = default);

    // PUT api/auth-requests/{id} — 审批方批准（或拒绝）指定 request。
    Task<AuthRequestResponse> ApproveAsync(string id, AuthResponseRequest request, CancellationToken ct = default);
}
