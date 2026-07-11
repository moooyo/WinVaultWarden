using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using App.Services;
using Core.BrowserIntegration;
using Core.Models;
using Core.Passkeys;
using Core.Services;
using Core.Session;
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
public class PasskeyBridgeServerMalformedFrameTests
{
    [Fact]
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
            Assert.Equal("vault_locked", response.Error?.Code);
        }
        finally
        {
            // StopAsync must complete without throwing even though a connection died on a
            // JsonException rather than one of the previously-handled transport exceptions.
            await server.StopAsync();
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

    // Minimal vault service reporting "locked": HandleAsync's first step is CaptureLease(), which it
    // throws InvalidOperationException from, so no other member is ever reached by this test.
    private sealed class LockedVaultService : IVaultService
    {
        public IVaultSnapshot Snapshot => throw new NotSupportedException();
        public IReadOnlyList<Cipher> GetCiphers() => throw new NotSupportedException();
        public IReadOnlyList<Folder> GetFolders() => throw new NotSupportedException();
        public IReadOnlyList<DeviceInfo> GetDevices() => throw new NotSupportedException();
        public VaultSessionLease CaptureLease() => throw new InvalidOperationException("Vault is locked.");
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
