using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Core.BrowserIntegration;

namespace Core.Passkeys;

/// <summary>
/// Fixed-order handshake envelope for the App&lt;-&gt;native-host passkey pipe (Task 3).
/// <c>V</c> is the protocol version, <c>Type</c> is <c>server.hello</c> or <c>client.hello</c>,
/// <c>Nonce</c> is the server's per-session random challenge, and <c>Pid</c> is the sender's own
/// process id (cross-checked against the kernel-reported peer PID — never trusted on its own).
/// </summary>
public sealed record PasskeyBridgeHello(
    [property: JsonPropertyName("v")] int V,
    string Type,
    string Nonce,
    uint Pid);

/// <summary>Result of a completed handshake: the verified peer plus the bound session nonce.</summary>
public sealed record PasskeyBridgeSession(PeerIdentity Peer, string Nonce);

/// <summary>
/// Nonce generation and the fixed-order handshake used by both bridge ends. Identity is always
/// verified before any hello is written, and every hello/session nonce comparison is length-checked
/// and constant-time. A fresh 32-byte nonce is minted per server session, so a hello echoing a
/// previous session's nonce (replay across a restart) never validates.
/// </summary>
public static class PasskeyBridgeHandshake
{
    public const int Version = 1;
    public const string ServerHelloType = "server.hello";
    public const string ClientHelloType = "client.hello";
    private const int NonceByteLength = 32;

    /// <summary>Mints an unpadded base64url encoding of 32 fresh random bytes.</summary>
    public static string CreateServerNonce() =>
        WebAuthnCrypto.EncodeBase64Url(RandomNumberGenerator.GetBytes(NonceByteLength));

    /// <summary>Server validates the client hello: right version/type, echoed nonce, authorized client PID.</summary>
    public static bool ValidateClientHello(
        PasskeyBridgeHello hello,
        string expectedNonce,
        uint authorizedClientPid) =>
        hello is { V: Version, Type: ClientHelloType }
        && hello.Pid == authorizedClientPid
        && NonceMatches(hello.Nonce, expectedNonce);

    /// <summary>Client validates the server hello: right version/type, 32-byte nonce, authorized server PID.</summary>
    public static bool ValidateServerHello(
        PasskeyBridgeHello hello,
        uint authorizedServerPid) =>
        hello is { V: Version, Type: ServerHelloType }
        && hello.Pid == authorizedServerPid
        && IsWellFormedNonce(hello.Nonce);

    /// <summary>Length-checked, constant-time nonce comparison.</summary>
    public static bool NonceMatches(string? actual, string expected)
    {
        if (actual is null || actual.Length != expected.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual),
            Encoding.ASCII.GetBytes(expected));
    }

    public static Task WriteHelloAsync(Stream stream, PasskeyBridgeHello hello, CancellationToken ct) =>
        BrowserPasskeyMessageProtocol.WriteAsync(stream, hello, PasskeyJsonContext.Default.PasskeyBridgeHello, ct);

    public static async Task<PasskeyBridgeHello> ReadHelloAsync(Stream stream, CancellationToken ct) =>
        await BrowserPasskeyMessageProtocol.ReadAsync(stream, PasskeyJsonContext.Default.PasskeyBridgeHello, ct)
            ?? throw new UnauthorizedAccessException("Passkey bridge handshake stream closed.");

    /// <summary>
    /// Server half: authorize the client first, only then write server.hello, then require a
    /// client.hello echoing the nonce and matching the authorized client PID.
    /// </summary>
    public static async Task<PasskeyBridgeSession> CompleteServerAsync(
        Stream stream,
        string sessionNonce,
        uint localProcessId,
        Func<CancellationToken, ValueTask<PeerIdentity>> authorizeClientAsync,
        CancellationToken ct)
    {
        var peer = await authorizeClientAsync(ct);
        await WriteHelloAsync(
            stream,
            new PasskeyBridgeHello(Version, ServerHelloType, sessionNonce, localProcessId),
            ct);
        var clientHello = await ReadHelloAsync(stream, ct);
        if (!ValidateClientHello(clientHello, sessionNonce, peer.ProcessId))
            throw new UnauthorizedAccessException("Passkey bridge client handshake rejected.");
        return new PasskeyBridgeSession(peer, sessionNonce);
    }

    /// <summary>
    /// Client half: authorize the server first, then read server.hello, validate it against the
    /// authorized server PID, and echo the nonce back with our own PID.
    /// </summary>
    public static async Task<PasskeyBridgeSession> CompleteClientAsync(
        Stream stream,
        uint localProcessId,
        Func<CancellationToken, ValueTask<PeerIdentity>> authorizeServerAsync,
        CancellationToken ct)
    {
        var peer = await authorizeServerAsync(ct);
        var serverHello = await ReadHelloAsync(stream, ct);
        if (!ValidateServerHello(serverHello, peer.ProcessId))
            throw new UnauthorizedAccessException("Passkey bridge server handshake rejected.");
        await WriteHelloAsync(
            stream,
            new PasskeyBridgeHello(Version, ClientHelloType, serverHello.Nonce, localProcessId),
            ct);
        return new PasskeyBridgeSession(peer, serverHello.Nonce);
    }

    private static bool IsWellFormedNonce(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce))
            return false;
        try
        {
            return WebAuthnCrypto.DecodeBase64Url(nonce).Length == NonceByteLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
