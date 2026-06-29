using Api;
using Api.Dtos;

namespace Vault.Tests;

// 手写 IAuthRequestApiClient 测试替身。捕获请求、返回预置响应，可选抛异常。
// 镜像 FakeAttachmentApiClient 写法。
public sealed class FakeAuthRequestApiClient : IAuthRequestApiClient
{
    // ── 调用记录 ──────────────────────────────────────────────────────────────
    public List<string> Calls { get; } = new();
    public string? LastApproveId { get; private set; }
    public AuthResponseRequest? LastApproveRequest { get; private set; }
    public string? LastGetResponseId { get; private set; }
    public string? LastGetResponseCode { get; private set; }
    public AuthRequestRequest? LastCreateRequest { get; private set; }

    // ── 预置响应 ──────────────────────────────────────────────────────────────
    public AuthRequestListResponse PendingResult { get; set; } =
        new(Data: new List<AuthRequestResponse>());

    public AuthRequestResponse ApproveResult { get; set; } =
        new(Id: "req-1", PublicKey: "", RequestDeviceType: "Windows",
            RequestIpAddress: "192.168.1.1", Key: null, MasterPasswordHash: null,
            CreationDate: "2024-01-01T00:00:00Z", ResponseDate: null,
            RequestApproved: true, Origin: null);

    // ── 可选强制抛异常 ────────────────────────────────────────────────────────
    /// <summary>若非 null，GetPendingAsync 抛此异常。</summary>
    public Exception? ThrowOnGetPending { get; set; }

    /// <summary>若非 null，ApproveAsync 抛此异常。</summary>
    public Exception? ThrowOnApprove { get; set; }

    // ── IAuthRequestApiClient ─────────────────────────────────────────────────
    public void SetBaseAddress(string baseUrl) { /* no-op in fake */ }

    public Task<AuthRequestListResponse> GetPendingAsync(CancellationToken ct = default)
    {
        Calls.Add("get-pending");
        if (ThrowOnGetPending is not null) throw ThrowOnGetPending;
        return Task.FromResult(PendingResult);
    }

    public Task<AuthRequestResponse> ApproveAsync(
        string id, AuthResponseRequest request, CancellationToken ct = default)
    {
        Calls.Add("approve");
        LastApproveId = id;
        LastApproveRequest = request;
        if (ThrowOnApprove is not null) throw ThrowOnApprove;
        return Task.FromResult(ApproveResult);
    }

    public Task<AuthRequestResponse> CreateAsync(
        AuthRequestRequest request, CancellationToken ct = default)
    {
        Calls.Add("create");
        LastCreateRequest = request;
        return Task.FromResult(ApproveResult);
    }

    public Task<AuthRequestResponse> GetResponseAsync(
        string id, string code, CancellationToken ct = default)
    {
        Calls.Add("get-response");
        LastGetResponseId = id;
        LastGetResponseCode = code;
        return Task.FromResult(ApproveResult);
    }
}
