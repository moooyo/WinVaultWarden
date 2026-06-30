using Core.Models;

namespace App.Services;

/// <summary>
/// IEmergencyAccessUiService 的内存替身，用于设计期和测试。不触网。
/// GetTrustedAsync 返回 ≥1 EmergencyContact；GetGrantedAsync 返回 ≥1 GrantedAccess；
/// ViewAsync 返回示例恢复密码库；其余写操作返回 Task.CompletedTask。
/// </summary>
public sealed class MockEmergencyAccessUiService : IEmergencyAccessUiService
{
    private readonly IReadOnlyList<EmergencyContact> _trusted =
    [
        new EmergencyContact(
            Id: "ec-1",
            GranteeId: "user-alice",
            Email: "alice@example.com",
            Name: "Alice",
            Status: EmergencyAccessStatus.Confirmed,
            Type: EmergencyAccessType.View,
            WaitTimeDays: 7),
        new EmergencyContact(
            Id: "ec-2",
            GranteeId: null,
            Email: "bob@example.com",
            Name: null,
            Status: EmergencyAccessStatus.Invited,
            Type: EmergencyAccessType.Takeover,
            WaitTimeDays: 14),
    ];

    private readonly IReadOnlyList<GrantedAccess> _granted =
    [
        new GrantedAccess(
            Id: "ga-1",
            GrantorId: "user-carol",
            Email: "carol@example.com",
            Name: "Carol",
            Status: EmergencyAccessStatus.Confirmed,
            Type: EmergencyAccessType.View,
            WaitTimeDays: 7),
    ];

    public Task<IReadOnlyList<EmergencyContact>> GetTrustedAsync(CancellationToken ct = default) =>
        Task.FromResult(_trusted);

    public Task InviteAsync(string email, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task ReinviteAsync(string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task ConfirmAsync(string id, string granteeId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UpdateAsync(string id, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RemoveAsync(string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task ApproveAsync(string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RejectAsync(string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<GrantedAccess>> GetGrantedAsync(CancellationToken ct = default) =>
        Task.FromResult(_granted);

    public Task AcceptAsync(string id, string token, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InitiateAsync(string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<RecoveredVault> ViewAsync(string id, string grantorEmail, CancellationToken ct = default) =>
        Task.FromResult(new RecoveredVault(
            GrantorEmail: string.IsNullOrWhiteSpace(grantorEmail) ? "carol@example.com" : grantorEmail,
            Ciphers: []));

    public Task TakeoverAndResetPasswordAsync(string id, string grantorEmail, string newPassword, CancellationToken ct = default) =>
        Task.CompletedTask;
}
