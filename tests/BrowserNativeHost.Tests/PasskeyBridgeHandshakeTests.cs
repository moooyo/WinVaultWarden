using Core.BrowserIntegration;
using Core.Passkeys;
using Xunit;

namespace BrowserNativeHost.Tests;

public sealed class PasskeyBridgeHandshakeTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private const string App = @"C:\Program Files\WinVaultWarden\App.exe";

    [Fact]
    public void NewServerNonce_Is32BytesBase64UrlAndRotates()
    {
        var first = PasskeyBridgeHandshake.CreateServerNonce();
        var second = PasskeyBridgeHandshake.CreateServerNonce();

        Assert.Equal(32, WebAuthnCrypto.DecodeBase64Url(first).Length);
        Assert.Equal(32, WebAuthnCrypto.DecodeBase64Url(second).Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ClientHello_MustEchoNonceAndAuthorizedPid()
    {
        var nonce = PasskeyBridgeHandshake.CreateServerNonce();
        var accepted = PasskeyBridgeHandshake.ValidateClientHello(
            new PasskeyBridgeHello(1, "client.hello", nonce, 5678),
            nonce,
            authorizedClientPid: 5678);
        var wrongNonce = PasskeyBridgeHandshake.ValidateClientHello(
            new PasskeyBridgeHello(1, "client.hello", "replayed", 5678),
            nonce,
            5678);
        var wrongPid = PasskeyBridgeHandshake.ValidateClientHello(
            new PasskeyBridgeHello(1, "client.hello", nonce, 9999),
            nonce,
            5678);

        Assert.True(accepted);
        Assert.False(wrongNonce);
        Assert.False(wrongPid);
    }

    [Fact]
    public void ClientHello_RejectsWrongVersionOrType()
    {
        var nonce = PasskeyBridgeHandshake.CreateServerNonce();
        Assert.False(PasskeyBridgeHandshake.ValidateClientHello(
            new PasskeyBridgeHello(2, "client.hello", nonce, 5678), nonce, 5678));
        Assert.False(PasskeyBridgeHandshake.ValidateClientHello(
            new PasskeyBridgeHello(1, "server.hello", nonce, 5678), nonce, 5678));
    }

    [Fact]
    public void ServerHello_MustCarry32ByteNonceAndAuthorizedPid()
    {
        var nonce = PasskeyBridgeHandshake.CreateServerNonce();
        Assert.True(PasskeyBridgeHandshake.ValidateServerHello(
            new PasskeyBridgeHello(1, "server.hello", nonce, 4321), authorizedServerPid: 4321));
        Assert.False(PasskeyBridgeHandshake.ValidateServerHello(
            new PasskeyBridgeHello(1, "server.hello", nonce, 4321), authorizedServerPid: 1));
        Assert.False(PasskeyBridgeHandshake.ValidateServerHello(
            new PasskeyBridgeHello(1, "server.hello", "short", 4321), authorizedServerPid: 4321));
    }

    // Restart rotation: a hello echoing a previous session's nonce must not validate against a new one.
    [Fact]
    public void RestartRotation_OldNonceRejectedByNewSession()
    {
        var previous = PasskeyBridgeHandshake.CreateServerNonce();
        var current = PasskeyBridgeHandshake.CreateServerNonce();
        Assert.False(PasskeyBridgeHandshake.ValidateClientHello(
            new PasskeyBridgeHello(1, "client.hello", previous, 5678), current, 5678));
    }

    // Identity must be verified before the server writes server.hello: two MemoryStreams drive the
    // handshake and the authorize delegate blocks, proving nothing is written until it completes.
    [Fact]
    public async Task Server_DoesNotWriteServerHello_UntilClientAuthorized()
    {
        var ct = TestContext.Current.CancellationToken;
        var nonce = PasskeyBridgeHandshake.CreateServerNonce();
        const uint clientPid = 4242;

        var readFrom = new MemoryStream();
        await PasskeyBridgeHandshake.WriteHelloAsync(
            readFrom, new PasskeyBridgeHello(1, "client.hello", nonce, clientPid), ct);
        readFrom.Position = 0;
        var writeTo = new MemoryStream();
        var duplex = new DuplexStream(readFrom, writeTo);

        var gate = new TaskCompletionSource();
        var task = PasskeyBridgeHandshake.CompleteServerAsync(
            duplex,
            nonce,
            localProcessId: 1000,
            async _ =>
            {
                await gate.Task;
                return Identity(clientPid, App);
            },
            ct);

        Assert.Equal(0, writeTo.Length); // server.hello must not precede identity verification.

        gate.SetResult();
        var session = await task;

        Assert.True(writeTo.Length > 0);
        Assert.Equal(clientPid, session.Peer.ProcessId);
        Assert.Equal(nonce, session.Nonce);
    }

    [Fact]
    public async Task ClientAndServer_CompleteHandshakeOverConnectedStreams()
    {
        var ct = TestContext.Current.CancellationToken;
        var nonce = PasskeyBridgeHandshake.CreateServerNonce();
        const uint serverPid = 700;
        const uint clientPid = 800;

        var serverToClient = new BlockingStream();
        var clientToServer = new BlockingStream();
        var serverPipe = new DuplexStream(clientToServer, serverToClient);
        var clientPipe = new DuplexStream(serverToClient, clientToServer);

        var serverTask = PasskeyBridgeHandshake.CompleteServerAsync(
            serverPipe, nonce, serverPid, _ => new ValueTask<PeerIdentity>(Identity(clientPid, App)), ct);
        var clientTask = PasskeyBridgeHandshake.CompleteClientAsync(
            clientPipe, clientPid, _ => new ValueTask<PeerIdentity>(Identity(serverPid, App)), ct);

        var server = await serverTask;
        var client = await clientTask;

        Assert.Equal(clientPid, server.Peer.ProcessId);
        Assert.Equal(serverPid, client.Peer.ProcessId);
        Assert.Equal(nonce, server.Nonce);
        Assert.Equal(nonce, client.Nonce);
    }

    [Fact]
    public void NonceMatches_UsesLengthCheckedComparison()
    {
        var nonce = PasskeyBridgeHandshake.CreateServerNonce();
        Assert.True(PasskeyBridgeHandshake.NonceMatches(nonce, nonce));
        Assert.False(PasskeyBridgeHandshake.NonceMatches(null, nonce));
        Assert.False(PasskeyBridgeHandshake.NonceMatches("", nonce));
        Assert.False(PasskeyBridgeHandshake.NonceMatches(nonce + "x", nonce));
    }

    private static PeerIdentity Identity(uint pid, string path) =>
        new(pid, Sid, path, "WinVaultWarden_abc", false, null);

    // Minimal duplex stream: reads come from one stream, writes go to another.
    private sealed class DuplexStream(Stream readFrom, Stream writeTo) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => writeTo.Flush();
        public override Task FlushAsync(CancellationToken ct) => writeTo.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => readFrom.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            readFrom.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => writeTo.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            writeTo.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // Async byte channel so both handshake halves can run concurrently against connected streams.
    private sealed class BlockingStream : Stream
    {
        private readonly SemaphoreSlim _available = new(0);
        private readonly Queue<byte> _buffer = new();
        private readonly object _gate = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.IsEmpty)
                return 0;
            await _available.WaitAsync(ct); // block until at least one byte is available
            lock (_gate)
            {
                var read = 0;
                buffer.Span[read++] = _buffer.Dequeue(); // consumes the permit acquired above
                // Drain any further buffered bytes, consuming one permit each (never blocks).
                while (read < buffer.Length && _buffer.Count > 0 && _available.Wait(0))
                    buffer.Span[read++] = _buffer.Dequeue();
                return read;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            lock (_gate)
            {
                foreach (var b in buffer.Span)
                {
                    _buffer.Enqueue(b);
                    _available.Release();
                }
            }
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
