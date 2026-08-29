namespace Core.Session;

public enum SessionTerminationReason
{
    InvalidRefreshToken,
    RemoteLogout,
}

/// <summary>
/// Identifies the already-invalidated session whose application state still needs to be cleared.
/// </summary>
public sealed record SessionTerminationNotice(
    SessionTerminationReason Reason,
    VaultSessionLease InvalidatedSession);

public interface ISessionTerminationSource
{
    event Action<SessionTerminationNotice>? TerminationRequested;
}

public interface ISessionTerminationSink
{
    void Request(SessionTerminationNotice notice);
}
