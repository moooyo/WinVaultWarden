using System.IO.Pipelines;
using System.Text.Json;
using Core.Passkeys;
using Xunit;

namespace BrowserNativeHost.Tests;

// The native host keeps a single persistent App pipe connection and multiplexes many request ids
// over it. These tests drive AppBridgeSession over an in-memory duplex (no pipes/handshake) and act
// as the App: they read inbound request/cancel frames and write response frames, verifying id
// routing, duplicate rejection, cancel fan-out, disconnect failure, and per-message nonce binding.
public class AppBridgeSessionTests
{
    private const string Nonce = "test-session-nonce-AAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task RoutesResponsesByIdEvenOutOfOrder()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = cts.Token;
        await using var duplex = new Duplex();
        await using var session = NewSession(duplex);

        var taskA = session.SendAsync(Request("0000000000000000000000000000000a"), ct);
        var first = await ReadInboundAsync(duplex.Server, ct);
        var taskB = session.SendAsync(Request("0000000000000000000000000000000b"), ct);
        var second = await ReadInboundAsync(duplex.Server, ct);

        // Answer the second-arrived request first to prove routing does not rely on arrival order.
        await WriteResponseAsync(duplex.Server, Response(second.Id!), ct);
        await WriteResponseAsync(duplex.Server, Response(first.Id!), ct);

        var resultA = await taskA;
        var resultB = await taskB;
        Assert.Equal("0000000000000000000000000000000a", resultA.Id);
        Assert.True(resultA.Ok);
        Assert.Equal("0000000000000000000000000000000b", resultB.Id);
        Assert.True(resultB.Ok);
    }

    [Fact]
    public async Task DuplicateId_ReturnsRequestInProgressWithoutSecondWrite()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = cts.Token;
        await using var duplex = new Duplex();
        await using var session = NewSession(duplex);

        var pending = session.SendAsync(Request("0000000000000000000000000000000c"), ct);
        var inbound = await ReadInboundAsync(duplex.Server, ct);
        Assert.Equal("0000000000000000000000000000000c", inbound.Id);

        // The first request is now registered; a same-id request must be rejected locally.
        var duplicate = await session.SendAsync(Request("0000000000000000000000000000000c"), ct);
        Assert.False(duplicate.Ok);
        Assert.Equal(PasskeyErrorCodes.RequestInProgress, duplicate.Error?.Code);

        await WriteResponseAsync(duplex.Server, Response("0000000000000000000000000000000c"), ct);
        var result = await pending;
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Cancellation_RemovesPendingAndWritesCancelFrame()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = cts.Token;
        await using var duplex = new Duplex();
        await using var session = NewSession(duplex);

        using var requestCts = new CancellationTokenSource();
        var task = session.SendAsync(Request("0000000000000000000000000000000d"), requestCts.Token);
        var inbound = await ReadInboundAsync(duplex.Server, ct);
        Assert.Equal("0000000000000000000000000000000d", inbound.Id);

        requestCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        var cancelFrame = await ReadInboundAsync(duplex.Server, ct);
        Assert.Equal("cancel", cancelFrame.Type);
        Assert.Equal("0000000000000000000000000000000d", cancelFrame.Id);
        Assert.Equal(Nonce, cancelFrame.Nonce);
    }

    [Fact]
    public async Task Disconnect_FailsPendingWithIoException()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = cts.Token;
        await using var duplex = new Duplex();
        await using var session = NewSession(duplex);

        var task = session.SendAsync(Request("0000000000000000000000000000000e"), ct);
        await ReadInboundAsync(duplex.Server, ct);

        duplex.CompleteServerToClient();
        await Assert.ThrowsAsync<IOException>(() => task);
    }

    // Browser-task-7 remediation (review L1): a failure raised by the connect FACTORY itself — this
    // request never reached a connected App at all (no listener yet, handshake failure, etc.) — must
    // be told apart from Disconnect_FailsPendingWithIoException above, where the connection WAS
    // established and only lost later. EnsureConnectedAsync wraps the former as
    // AppBridgeConnectFailedException so NativeMessagingSession can map only this case to
    // client_not_handled (the sole fallback trigger) while the latter stays a plain IOException and
    // maps to operation_aborted (never falls back).
    [Fact]
    public async Task ConnectFactoryFailure_WrapsAsAppBridgeConnectFailedException()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = cts.Token;
        var inner = new TimeoutException("Timed out connecting to the WinVaultWarden passkey bridge.");
        await using var session = new AppBridgeSession(_ => throw inner);

        var wrapped = await Assert.ThrowsAsync<AppBridgeConnectFailedException>(
            () => session.SendAsync(Request("00000000000000000000000000000f0"), ct));
        Assert.Same(inner, wrapped.InnerException);
    }

    [Fact]
    public async Task ConnectFactoryCancellation_PropagatesAsOperationCanceled_NotWrapped()
    {
        // A caller walking away during connect (external ct/disposal) is a cancel, not "the App isn't
        // there": it must not be wrapped as AppBridgeConnectFailedException (which would incorrectly
        // become a fallback-triggering client_not_handled).
        using var cts = new CancellationTokenSource();
        await using var session = new AppBridgeSession(async innerCt =>
        {
            cts.Cancel();
            innerCt.ThrowIfCancellationRequested();
            await Task.Delay(Timeout.Infinite, innerCt);
            throw new InvalidOperationException("unreachable");
        });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => session.SendAsync(Request("00000000000000000000000000000f1"), cts.Token));
    }

    [Fact]
    public async Task Request_CarriesSessionNonce()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = cts.Token;
        await using var duplex = new Duplex();
        await using var session = NewSession(duplex);

        var task = session.SendAsync(Request("0000000000000000000000000000000f"), ct);
        var inbound = await ReadInboundAsync(duplex.Server, ct);
        Assert.Equal(Nonce, inbound.Nonce);
        Assert.Equal(1, inbound.Version);

        await WriteResponseAsync(duplex.Server, Response("0000000000000000000000000000000f"), ct);
        await task;
    }

    [Fact]
    public async Task CancelAsync_WritesCancelEnvelopeWithNonce()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = cts.Token;
        await using var duplex = new Duplex();
        await using var session = NewSession(duplex);

        // Establish the connection with a live request first, then cancel a different id.
        var task = session.SendAsync(Request("00000000000000000000000000000010"), ct);
        await ReadInboundAsync(duplex.Server, ct);

        await session.CancelAsync("00000000000000000000000000000011", ct);
        var cancelFrame = await ReadInboundAsync(duplex.Server, ct);
        Assert.Equal("cancel", cancelFrame.Type);
        Assert.Equal("00000000000000000000000000000011", cancelFrame.Id);
        Assert.Equal(Nonce, cancelFrame.Nonce);

        await WriteResponseAsync(duplex.Server, Response("00000000000000000000000000000010"), ct);
        await task;
    }

    [Fact]
    public async Task ResponseNonceMismatch_FaultsConnection()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = cts.Token;
        await using var duplex = new Duplex();
        await using var session = NewSession(duplex);

        var task = session.SendAsync(Request("00000000000000000000000000000012"), ct);
        await ReadInboundAsync(duplex.Server, ct);

        // A response bound to a different nonce must never satisfy the pending request.
        await WriteResponseAsync(
            duplex.Server,
            Response("00000000000000000000000000000012") with { Nonce = "a-different-nonce-BBBBBBBBBBBBBBBBBBBBBBBBBB" },
            ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => task);
    }

    private static AppBridgeSession NewSession(Duplex duplex) =>
        new(_ => Task.FromResult(new AppBridgeConnection(duplex.Client, Nonce, NoopAsyncDisposable.Instance)));

    private static BrowserPasskeyRequest Request(string id) =>
        new(id, "passkey.get", GetPayload(), DeadlineUnixMs: 1,
            Context: new BrowserRequestContext("https://example.com", "https://example.com", 1, 0, "doc", true));

    private static BrowserPasskeyResponse Response(string id) =>
        new(id, "passkey.get", true, Nonce: Nonce);

    private static JsonElement GetPayload()
    {
        using var doc = JsonDocument.Parse(
            """{"origin":"https://example.com","challenge":"AQIDBA","allowCredentials":[]}""");
        return doc.RootElement.Clone();
    }

    private static async Task<BrowserPasskeyRequest> ReadInboundAsync(Stream server, CancellationToken ct) =>
        await BrowserPasskeyMessageProtocol.ReadAsync(server, PasskeyJsonContext.Default.BrowserPasskeyRequest, ct)
            ?? throw new EndOfStreamException("App bridge inbound stream closed.");

    private static Task WriteResponseAsync(Stream server, BrowserPasskeyResponse response, CancellationToken ct) =>
        BrowserPasskeyMessageProtocol.WriteAsync(server, response, PasskeyJsonContext.Default.BrowserPasskeyResponse, ct);

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static readonly NoopAsyncDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Bidirectional in-memory transport built from two one-way pipes.
    private sealed class Duplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();

        public Duplex()
        {
            Client = new DuplexStream(_serverToClient.Reader.AsStream(), _clientToServer.Writer.AsStream());
            Server = new DuplexStream(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream());
        }

        public Stream Client { get; }
        public Stream Server { get; }

        public void CompleteServerToClient() => _serverToClient.Writer.Complete();

        public ValueTask DisposeAsync()
        {
            _clientToServer.Writer.Complete();
            _serverToClient.Writer.Complete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            read.ReadAsync(buffer, ct);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            read.ReadAsync(buffer, offset, count, ct);

        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            write.WriteAsync(buffer, ct);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            write.WriteAsync(buffer, offset, count, ct);

        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);

        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
