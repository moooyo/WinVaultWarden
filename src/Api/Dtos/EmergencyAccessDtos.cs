using System.Text.Json.Serialization;

namespace Api.Dtos;

// ===== 响应（camelCase 由 ApiJsonContext 统一）=====
public sealed class EmergencyAccessGranteeDetailsDto
{
    public string Id { get; init; } = string.Empty;
    public int Status { get; init; }
    public int Type { get; init; }
    public int WaitTimeDays { get; init; }
    public string? GranteeId { get; init; }
    public string? Email { get; init; }
    public string? Name { get; init; }
    public string? AvatarColor { get; init; }
}

public sealed class EmergencyAccessGrantorDetailsDto
{
    public string Id { get; init; } = string.Empty;
    public int Status { get; init; }
    public int Type { get; init; }
    public int WaitTimeDays { get; init; }
    public string? GrantorId { get; init; }
    public string? Email { get; init; }
    public string? Name { get; init; }
    public string? AvatarColor { get; init; }
}

public sealed class PublicKeyResponse
{
    public string? UserId { get; init; }
    public string? PublicKey { get; init; }
}

public sealed class EmergencyAccessDto
{
    public string Id { get; init; } = string.Empty;
    public int Status { get; init; }
    public int Type { get; init; }
    public int WaitTimeDays { get; init; }
}

public sealed class EmergencyAccessViewResponse
{
    public CipherDto[]? Ciphers { get; init; }
    public string? KeyEncrypted { get; init; }
}

public sealed class EmergencyAccessTakeoverResponse
{
    public int Kdf { get; init; }
    public int KdfIterations { get; init; }
    public int? KdfMemory { get; init; }
    public int? KdfParallelism { get; init; }
    public string? KeyEncrypted { get; init; }
}

// ===== 请求 =====
public sealed class EmergencyAccessInviteRequest
{
    public string Email { get; init; } = string.Empty;
    public int Type { get; init; }
    public int WaitTimeDays { get; init; }
}

public sealed class EmergencyAccessConfirmRequest
{
    public string Key { get; init; } = string.Empty;
}

public sealed class EmergencyAccessUpdateRequest
{
    public int Type { get; init; }
    public int WaitTimeDays { get; init; }
    public string? KeyEncrypted { get; init; }
}

public sealed class EmergencyAccessAcceptRequest
{
    public string Token { get; init; } = string.Empty;
}

public sealed class EmergencyAccessPasswordRequest
{
    public string NewMasterPasswordHash { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
}

public sealed class DeleteAccountRequest
{
    public string MasterPasswordHash { get; init; } = string.Empty;
}
