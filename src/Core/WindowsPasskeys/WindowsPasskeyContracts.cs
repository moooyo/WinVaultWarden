using Core.Services;
using Core.Session;

namespace Core.WindowsPasskeys;

public static class WindowsPasskeyIdentity
{
    public static readonly Guid ClassId = new("f8ad5a01-0d69-47ca-a928-358d16a07575");
    public static readonly Guid Aaguid = new("04d34d55-b26f-40f5-8064-a09360fbf623");
    public const string DisplayName = "WinVaultWarden";
    public const string PluginRpId = "winvaultwarden.local";
    public const string ActivationArgument = "--windows-passkey-provider";
    public const int MaximumRequestBytes = 1024 * 1024;
    public const int MaximumCredentialIdBytes = 1024;
    public const int MaximumCredentialCount = 128;
}

public enum WindowsPasskeyOperation
{
    MakeCredential,
    GetAssertion,
}

/// <summary>
/// An operation decoded from the platform-signed CTAP request. Windows validates the
/// WebAuthn origin; an authenticator receives an RP ID and a client-data hash, not an origin.
/// Only the native entry point may construct this object from an external request.
/// </summary>
public sealed record WindowsPasskeyRequest(
    Guid TransactionId,
    WindowsPasskeyOperation Operation,
    string RpId,
    byte[] ClientDataHash,
    bool RequireUserVerification,
    IReadOnlyList<byte[]> CredentialIds,
    string? RpName = null,
    byte[]? UserId = null,
    string? UserName = null,
    string? UserDisplayName = null,
    IReadOnlyList<int>? Algorithms = null);

public sealed record WindowsPasskeyApprovalRequest(
    Guid TransactionId,
    string RpId,
    string? RpName,
    string? UserName,
    string? UserDisplayName,
    bool RequireUserVerification,
    IReadOnlyList<PasskeyCredentialTarget> Candidates);

public sealed record WindowsPasskeyApproval(
    PasskeyApprovalEvidence Evidence,
    string? CipherId = null,
    string? CredentialId = null);

public interface IWindowsPasskeyApprovalService
{
    Task<bool> EnsureUnlockedAsync(CancellationToken ct);

    Task<WindowsPasskeyApproval> ConfirmCreateAsync(
        WindowsPasskeyApprovalRequest request,
        CancellationToken ct);

    Task<WindowsPasskeyApproval> ConfirmUseAsync(
        WindowsPasskeyApprovalRequest request,
        CancellationToken ct);
}

public sealed record WindowsPasskeyCredentialMetadata(
    byte[] CredentialId,
    string RpId,
    string RpName,
    byte[] UserId,
    string? UserName,
    string? UserDisplayName);

public sealed record WindowsPasskeyResult(byte[] EncodedResponse, VaultSessionLease Lease);

/// <summary>HRESULT values used by the documented plugin COM contract.</summary>
public sealed class WindowsPasskeyException(int errorCode, string message) : Exception(message)
{
    public const int InvalidRequest = unchecked((int)0x80070057);
    public const int NotSupported = unchecked((int)0x80090029);
    public const int UserCancelled = unchecked((int)0x80090036);
    public const int NotFound = unchecked((int)0x80090011);
    public const int AlreadyExists = unchecked((int)0x8009000F);
    public const int Busy = unchecked((int)0x800700AA);
    public int ErrorCode { get; } = errorCode;
}
