using System.IO.Pipes;

namespace Core.BrowserIntegration;

/// <summary>
/// Mutually authenticates the App (pipe server) and native host (pipe client) before any bridge
/// traffic (Task 3). Each side derives the peer PID from the pipe kernel object — never from a
/// self-reported hello — and rejects a mismatched identity by throwing.
/// </summary>
public interface IPipePeerAuthorizer
{
    /// <summary>Server side: authorize the client that connected to <paramref name="pipe"/>.</summary>
    ValueTask<PeerIdentity> AuthorizeClientAsync(
        NamedPipeServerStream pipe,
        string expectedExecutablePath,
        CancellationToken ct);

    /// <summary>Client side: authorize the server that <paramref name="pipe"/> connected to.</summary>
    ValueTask<PeerIdentity> AuthorizeServerAsync(
        NamedPipeClientStream pipe,
        string expectedExecutablePath,
        CancellationToken ct);
}

/// <summary>
/// Resolves Windows process identities and pipe peer PIDs. Split from the authorizer so the
/// bidirectional PID sourcing and policy wiring are unit-testable with a fake, while the real
/// Win32/P-Invoke reader lives in each host (App vs native host) next to its own NativeMethods.
/// </summary>
public interface IProcessIdentityReader
{
    /// <summary>Identity of the current (local) process.</summary>
    PeerIdentity ReadCurrentProcess();

    /// <summary>Identity of an arbitrary process by PID; throws <see cref="UnauthorizedAccessException"/> on failure.</summary>
    PeerIdentity ReadProcess(uint processId);

    /// <summary>Server PID backing a client pipe (GetNamedPipeServerProcessId).</summary>
    uint GetServerProcessId(NamedPipeClientStream pipe);

    /// <summary>Client PID backing a server pipe (GetNamedPipeClientProcessId).</summary>
    uint GetClientProcessId(NamedPipeServerStream pipe);
}

/// <summary>
/// Shared authorizer: takes the kernel-reported peer PID, reads both identities, and enforces
/// <see cref="PeerIdentityPolicy"/>. Concrete subclasses only supply the platform reader.
/// </summary>
public abstract class PipePeerAuthorizer : IPipePeerAuthorizer
{
    private readonly IProcessIdentityReader _reader;

    protected PipePeerAuthorizer(IProcessIdentityReader reader) => _reader = reader;

    public ValueTask<PeerIdentity> AuthorizeClientAsync(
        NamedPipeServerStream pipe,
        string expectedExecutablePath,
        CancellationToken ct) =>
        Authorize(_reader.GetClientProcessId(pipe), expectedExecutablePath, ct);

    public ValueTask<PeerIdentity> AuthorizeServerAsync(
        NamedPipeClientStream pipe,
        string expectedExecutablePath,
        CancellationToken ct) =>
        Authorize(_reader.GetServerProcessId(pipe), expectedExecutablePath, ct);

    private ValueTask<PeerIdentity> Authorize(
        uint peerProcessId,
        string expectedExecutablePath,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var local = _reader.ReadCurrentProcess();
        var peer = _reader.ReadProcess(peerProcessId);
        if (!PeerIdentityPolicy.IsAuthorized(local, peer, expectedExecutablePath))
            throw new UnauthorizedAccessException("Passkey bridge peer identity rejected.");
        return ValueTask.FromResult(peer);
    }
}
