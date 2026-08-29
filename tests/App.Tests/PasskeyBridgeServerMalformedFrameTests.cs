using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using App.Services;
using Core.BrowserIntegration;
using Core.Models;
using Core.Passkeys;
using Core.Services;
using Core.Session;
using Vault;
using Xunit;

namespace App.Tests;

// M1 remediation: the App-side passkey bridge read loop (RunConnectionAsync) must treat a malformed
// (non-JSON) frame as fatal for *only* that authenticated connection. Before the fix, the read loop's
// catch list covered IOException/InvalidDataException/EndOfStreamException but not JsonException —
// the most common "corrupt frame" outcome, since BrowserPasskeyMessageProtocol.ReadAsync deserializes
// with System.Text.Json. An uncaught JsonException would propagate out of RunConnectionAsync into
// HandleConnectionAsync (which must never throw), then into ListenAsync's Task.WhenAll(pending), and
// finally into StopAsync's `await listen!`, faulting the listener/shutdown instead of just the one
// connection. This test drives the real pipe/handshake and asserts: the bad connection is torn down
// cleanly, a subsequent connection is still accepted and fully served, and StopAsync does not throw.
public partial class PasskeyBridgeServerMalformedFrameTests
{
    [WindowsFact]
    public async Task MalformedFrame_ClosesOnlyThatConnection_ListenerKeepsAcceptingAndStopsCleanly()
    {
        var handler = new BrowserPasskeyRequestHandler(
            new LockedVaultService(), new UnusedVaultWriteService(), new UnusedApprovalService());
        var server = new PasskeyBridgeServer(handler, new AllowAllPeerAuthorizer());
        server.Start();
        try
        {
            // First connection: complete the real handshake, then write a frame whose body is not
            // valid JSON at all. The server must tear down only this connection, without throwing
            // out of the accept loop.
            await using (var first = await ConnectAsync())
            {
                await WriteMalformedFrameAsync(first.Pipe);

                var buffer = new byte[4];
                int read;
                try
                {
                    read = await first.Pipe.ReadAsync(buffer, CancellationToken.None);
                }
                catch (IOException)
                {
                    // A broken pipe can also surface as IOException on the client read depending on
                    // how the server end was torn down; either way nothing else was written back.
                    read = 0;
                }

                Assert.Equal(0, read);
            }

            // Second connection: the listener must still accept and fully serve a brand-new
            // connection (handshake + one request/response round trip) after the first one died.
            await using var second = await ConnectAsync();
            const string requestId = "00000000000000000000000000000099";
            await BrowserPasskeyMessageProtocol.WriteAsync(
                second.Pipe,
                new BrowserPasskeyRequest(requestId, "passkey.get", GetPayload(), Nonce: second.Nonce),
                PasskeyJsonContext.Default.BrowserPasskeyRequest,
                CancellationToken.None);

            var response = await BrowserPasskeyMessageProtocol.ReadAsync(
                second.Pipe,
                PasskeyJsonContext.Default.BrowserPasskeyResponse,
                CancellationToken.None);

            Assert.NotNull(response);
            Assert.Equal(requestId, response!.Id);
            Assert.False(response.Ok);
            Assert.Equal("client_not_handled", response.Error?.Code);
        }
        finally
        {
            // StopAsync must complete without throwing even though a connection died on a
            // JsonException rather than one of the previously-handled transport exceptions.
            await server.StopAsync();
        }
    }

    // Browser-task-7 remediation (review L5): drives a REAL handler exception through the real
    // App-side pipe (HandleRequestAsync's catch-all in PasskeyBridgeServer), not just a structural
    // argument. ThrowOnceVaultService throws a sentinel-laden InvalidOperationException from
    // Snapshot (uncaught anywhere inside BrowserPasskeyRequestHandler.HandleAsync, since only
    // CaptureLease's InvalidOperationException is caught there) on the FIRST call only, so the pipe
    // response for that request must be strictly {"code":"internal_error"} — no message/sentinel/
    // path/token/stack anywhere in the wire bytes — and the SAME connection must still serve a second,
    // ordinary request afterwards (proving the read loop wasn't torn down), and Start/StopAsync must
    // remain usable (a fresh connection round-trips after a restart).
    [WindowsFact]
    public async Task HandlerThrows_PipeResponseIsCodeOnlyInternalError_ConnectionAndStopStayUsable()
    {
        const string sentinel = @"SENTINEL_INTERNAL C:\Users\alice\vault token=secret stack=BrowserPasskeyRequestHandler";
        var handler = new BrowserPasskeyRequestHandler(
            new ThrowOnceVaultService(sentinel), new UnusedVaultWriteService(), new UnusedApprovalService());
        var server = new PasskeyBridgeServer(handler, new AllowAllPeerAuthorizer());
        server.Start();
        try
        {
            await using (var connection = await ConnectAsync())
            {
                const string firstId = "0000000000000000000000000000009a";
                await BrowserPasskeyMessageProtocol.WriteAsync(
                    connection.Pipe,
                    new BrowserPasskeyRequest(firstId, "passkey.get", GetPayload(), Nonce: connection.Nonce),
                    PasskeyJsonContext.Default.BrowserPasskeyRequest,
                    CancellationToken.None);

                var firstFrame = await ReadRawFrameAsync(connection.Pipe);
                var firstText = Encoding.UTF8.GetString(firstFrame);
                foreach (var forbidden in new[] { "SENTINEL_INTERNAL", @"C:\Users", "token=secret", "stack=" })
                    Assert.DoesNotContain(forbidden, firstText, StringComparison.Ordinal);
                Assert.DoesNotContain("message", firstText, StringComparison.OrdinalIgnoreCase);

                var firstResponse = JsonSerializer.Deserialize(
                    firstFrame, PasskeyJsonContext.Default.BrowserPasskeyResponse);
                Assert.NotNull(firstResponse);
                Assert.Equal(firstId, firstResponse!.Id);
                Assert.False(firstResponse.Ok);
                Assert.Equal(PasskeyErrorCodes.InternalError, firstResponse.Error?.Code);
                Assert.Null(firstResponse.Error?.Message);

                // Same connection, second request: the sentinel throw must not have torn down the
                // read loop or the connection. ThrowOnceVaultService now reports "locked" normally.
                const string secondId = "0000000000000000000000000000009b";
                await BrowserPasskeyMessageProtocol.WriteAsync(
                    connection.Pipe,
                    new BrowserPasskeyRequest(secondId, "passkey.get", GetPayload(), Nonce: connection.Nonce),
                    PasskeyJsonContext.Default.BrowserPasskeyRequest,
                    CancellationToken.None);

                var secondResponse = await BrowserPasskeyMessageProtocol.ReadAsync(
                    connection.Pipe, PasskeyJsonContext.Default.BrowserPasskeyResponse, CancellationToken.None);
                Assert.NotNull(secondResponse);
                Assert.Equal(secondId, secondResponse!.Id);
                Assert.False(secondResponse.Ok);
                Assert.Equal("client_not_handled", secondResponse.Error?.Code);
            }
        }
        finally
        {
            await server.StopAsync();
        }

        // Start/Stop remain usable after a sentinel failure: a full restart still round-trips.
        server.Start();
        try
        {
            await using var restarted = await ConnectAsync();
            const string thirdId = "0000000000000000000000000000009c";
            await BrowserPasskeyMessageProtocol.WriteAsync(
                restarted.Pipe,
                new BrowserPasskeyRequest(thirdId, "passkey.get", GetPayload(), Nonce: restarted.Nonce),
                PasskeyJsonContext.Default.BrowserPasskeyRequest,
                CancellationToken.None);

            var thirdResponse = await BrowserPasskeyMessageProtocol.ReadAsync(
                restarted.Pipe, PasskeyJsonContext.Default.BrowserPasskeyResponse, CancellationToken.None);
            Assert.NotNull(thirdResponse);
            Assert.Equal(thirdId, thirdResponse!.Id);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static async Task<byte[]> ReadRawFrameAsync(Stream pipe)
    {
        var lengthBytes = new byte[4];
        await ReadExactAsync(pipe, lengthBytes);
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        var payload = new byte[length];
        await ReadExactAsync(pipe, payload);
        return payload;
    }

    private static async Task ReadExactAsync(Stream pipe, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await pipe.ReadAsync(buffer.AsMemory(total, buffer.Length - total));
            if (read == 0)
                throw new EndOfStreamException("Pipe closed before the expected frame arrived.");
            total += read;
        }
    }

    private static async Task<ConnectedClient> ConnectAsync()
    {
        var pipe = new NamedPipeClientStream(
            ".", BrowserPasskeyBridge.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(connectTimeout.Token);

        var authorizer = new AllowAllPeerAuthorizer();
        var session = await PasskeyBridgeHandshake.CompleteClientAsync(
            pipe,
            (uint)Environment.ProcessId,
            ct => authorizer.AuthorizeServerAsync(pipe, string.Empty, ct),
            connectTimeout.Token);
        return new ConnectedClient(pipe, session.Nonce);
    }

    private static async Task WriteMalformedFrameAsync(Stream pipe)
    {
        var payload = "not-json"u8.ToArray();
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)payload.Length);
        await pipe.WriteAsync(lengthBytes);
        await pipe.WriteAsync(payload);
        await pipe.FlushAsync();
    }

    private static JsonElement GetPayload()
    {
        using var doc = JsonDocument.Parse(
            """{"origin":"https://example.com","challenge":"AQIDBA","allowCredentials":[]}""");
        return doc.RootElement.Clone();
    }

    private sealed class ConnectedClient(NamedPipeClientStream pipe, string nonce) : IAsyncDisposable
    {
        public NamedPipeClientStream Pipe { get; } = pipe;
        public string Nonce { get; } = nonce;

        public ValueTask DisposeAsync() => Pipe.DisposeAsync();
    }

    // Test-only authorizer: skips real Windows PID/signature verification (covered separately by
    // PeerAuthorizationTests) so this test can focus purely on the read-loop's frame handling.
    private sealed class AllowAllPeerAuthorizer : IPipePeerAuthorizer
    {
        private static readonly PeerIdentity Identity = new(
            (uint)Environment.ProcessId, "S-1-5-21-0-0-0-1000", string.Empty, null, false, null);

        public ValueTask<PeerIdentity> AuthorizeClientAsync(
            NamedPipeServerStream pipe, string expectedExecutablePath, CancellationToken ct) =>
            ValueTask.FromResult(Identity);

        public ValueTask<PeerIdentity> AuthorizeServerAsync(
            NamedPipeClientStream pipe, string expectedExecutablePath, CancellationToken ct) =>
            ValueTask.FromResult(Identity);
    }

    // Minimal vault service reporting "locked": Snapshot.State is not Unlocked, so HandleAsync's
    // first check (before it would ever reach CaptureLease) returns client_not_handled up front.
    private sealed class LockedVaultService : IVaultService
    {
        // A logged-out/locked session: State is not Unlocked, so the handler returns client_not_handled
        // up front (before it would ever reach CaptureLease).
        private readonly VaultSession _session = new();
        public IVaultSnapshot Snapshot => _session;
        public IReadOnlyList<Cipher> GetCiphers() => throw new NotSupportedException();
        public IReadOnlyList<Folder> GetFolders() => throw new NotSupportedException();
        public IReadOnlyList<DeviceInfo> GetDevices() => throw new NotSupportedException();
        public VaultSessionLease CaptureLease() => throw new InvalidOperationException("Vault is locked.");
        public bool IsCurrent(VaultSessionLease lease) => throw new NotSupportedException();
        public void ThrowIfExpired(VaultSessionLease lease) => throw new NotSupportedException();
        public IReadOnlyList<Cipher> GetCiphers(VaultSessionLease lease) => throw new NotSupportedException();
    }

    // Throws the given (sentinel-laden) InvalidOperationException from Snapshot on the FIRST access
    // only — HandleAsync's `_vault.Snapshot.State != VaultState.Unlocked` check is not wrapped by any
    // try/catch, so this reaches PasskeyBridgeServer.HandleRequestAsync's catch-all uncaught. Every
    // access after the first behaves like an ordinary locked/logged-out session (client_not_handled),
    // proving the sentinel throw did not wedge the connection or the service.
    private sealed class ThrowOnceVaultService(string sentinelMessage) : IVaultService
    {
        private readonly VaultSession _session = new();
        private bool _thrown;

        public IVaultSnapshot Snapshot
        {
            get
            {
                if (!_thrown)
                {
                    _thrown = true;
                    throw new InvalidOperationException(sentinelMessage);
                }
                return _session;
            }
        }

        public IReadOnlyList<Cipher> GetCiphers() => throw new NotSupportedException();
        public IReadOnlyList<Folder> GetFolders() => throw new NotSupportedException();
        public IReadOnlyList<DeviceInfo> GetDevices() => throw new NotSupportedException();
        public VaultSessionLease CaptureLease() => throw new NotSupportedException();
        public bool IsCurrent(VaultSessionLease lease) => throw new NotSupportedException();
        public void ThrowIfExpired(VaultSessionLease lease) => throw new NotSupportedException();
        public IReadOnlyList<Cipher> GetCiphers(VaultSessionLease lease) => throw new NotSupportedException();
    }

    private sealed class UnusedVaultWriteService : IVaultWriteService
    {
        public Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteCipherAsync(string cipherId, bool permanent, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedApprovalService : IPasskeyApprovalService
    {
        public Task<PasskeyUseApprovalResult> ConfirmUseAsync(
            PasskeyApprovalRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PasskeyCreateApprovalResult> ConfirmCreateAsync(
            PasskeyCreateApprovalRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
