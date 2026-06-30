namespace Core.Models;

public enum EmergencyAccessType { View = 0, Takeover = 1 }

public enum EmergencyAccessStatus
{
    Invited = 0,
    Accepted = 1,
    Confirmed = 2,
    RecoveryInitiated = 3,
    RecoveryApproved = 4,
}

// 授予方视角：我授权出去的紧急联系人
public sealed record EmergencyContact(
    string Id, string? GranteeId, string? Email, string? Name,
    EmergencyAccessStatus Status, EmergencyAccessType Type, int WaitTimeDays);

// 受托方视角：信任我的账户
public sealed record GrantedAccess(
    string Id, string? GrantorId, string? Email, string? Name,
    EmergencyAccessStatus Status, EmergencyAccessType Type, int WaitTimeDays);

// 受托方 View 解密结果（只读）
public sealed record RecoveredVault(string GrantorEmail, IReadOnlyList<Cipher> Ciphers);
