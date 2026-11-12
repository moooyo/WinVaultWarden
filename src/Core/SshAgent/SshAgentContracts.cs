namespace Core.SshAgent;

/// <summary>Kernel-derived information about the process connected to the agent pipe.</summary>
public sealed record SshAgentClientInfo(
    uint ProcessId,
    string ExecutablePath,
    DateTimeOffset StartedAt);

/// <summary>The host retains the process handle until the connection has been closed.</summary>
public interface ISshAgentClientIdentity : IDisposable
{
    SshAgentClientInfo Info { get; }
    bool IsCurrent { get; }
}

public sealed record SshAgentIdentity(byte[] PublicKeyBlob, string Comment);

/// <summary>Approval metadata never contains a private key or the raw signed data.</summary>
public sealed record SshAgentApprovalRequest(
    string CipherId,
    string KeyName,
    string Algorithm,
    string Fingerprint,
    SshAgentClientInfo Client,
    int DataLength,
    string DataFingerprint,
    bool RequiresMasterPassword);

public interface ISshAgentApprovalService
{
    /// <summary>Must obtain an explicit approval and fresh user verification for every signature.</summary>
    Task<bool> ConfirmAsync(SshAgentApprovalRequest request, CancellationToken ct = default);
}

public interface ISshAgentRequestHandler
{
    Task<IReadOnlyList<SshAgentIdentity>> GetIdentitiesAsync(
        ISshAgentClientIdentity client,
        CancellationToken ct = default);

    /// <summary>Returns a complete SSH signature blob, or null when the operation is denied.</summary>
    Task<byte[]?> SignAsync(
        ISshAgentClientIdentity client,
        byte[] publicKeyBlob,
        byte[] data,
        uint flags,
        CancellationToken ct = default);
}
