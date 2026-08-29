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
    public async Task PendingLimit_RejectsNinthAndReusesReleasedSlot()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = cts.Token;
        await using var duplex = new Duplex();
        await using var session = NewSession(duplex);
        var ids = Enumerable.Range(1, AppBridgeSession.MaxPendingRequests + 2)
            .Select(value => value.ToString("x32"))
            .ToArray();
        var pending = new List<Task<BrowserPasskeyResponse>>();

        foreach (var id in ids.Take(AppBridgeSession.MaxPendingRequests))
        {
            pending.Add(session.SendAsync(Request(id), ct));
            Assert.Equal(id, (await ReadInboundAsync(duplex.Server, ct)).Id);
        }

        var rejected = await session.SendAsync(Request(ids[AppBridgeSession.MaxPendingRequests]), ct);
        Assert.False(rejected.Ok);
        Assert.Equal(PasskeyErrorCodes.RateLimited, rejected.Error?.Code);

        await WriteResponseAsync(duplex.Server, Response(ids[0]), ct);
        Assert.True((await pending[0]).Ok);

        var replacement = session.SendAsync(Request(ids[^1]), ct);
        Assert.Equal(ids[^1], (await ReadInboundAsync(duplex.Server, ct)).Id);

        foreach (var id in ids.Skip(1).Take(AppBridgeSession.MaxPendingRequests - 1))
            await WriteResponseAsync(duplex.Server, Response(id), ct);
        await WriteResponseAsync(duplex.Server, Response(ids[^1]), ct);

        var completed = await Task.WhenAll(pending.Skip(1).Append(replacement));
        Assert.All(completed, response => Assert.True(response.Ok));
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

    [Fact]
    public async Task TeardownRace_DoesNotFailRequestReservedForReplacementConnection()
    {
        const string replacementNonce = "replacement-nonce-BBBBBBBBBBBBBBBBBBBBBBBBBB";
        const string firstId = "00000000000000000000000000000031";
        const string replacementId = "00000000000000000000000000000032";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = cts.Token;
        await using var first = new Duplex();
        await using var replacement = new Duplex();
        var teardownEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTeardown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementSlotReserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardownCount = 0;
        var connectCount = 0;
        var hooks = new AppBridgeSessionTestHooks
        {
            PendingSlotReserved = id =>
            {
                if (id == replacementId)
                    replacementSlotReserved.TrySetResult();
            },
            ConnectionFaultedBeforePendingFailureAsync = () =>
            {
                if (Interlocked.Increment(ref teardownCount) != 1)
                    return Task.CompletedTask;

                teardownEntered.TrySetResult();
                return releaseTeardown.Task;
            },
        };
        await using var session = new AppBridgeSession(
            _ => Interlocked.Increment(ref connectCount) switch
            {
                1 => Task.FromResult(new AppBridgeConnection(first.Client, Nonce, NoopAsyncDisposable.Instance)),
                2 => Task.FromResult(new AppBridgeConnection(
                    replacement.Client,
                    replacementNonce,
                    NoopAsyncDisposable.Instance)),
                _ => throw new InvalidOperationException("Unexpected App bridge reconnect."),
            },
            hooks);

        var disconnected = session.SendAsync(Request(firstId), ct);
        Assert.Equal(firstId, (await ReadInboundAsync(first.Server, ct)).Id);
        first.CompleteServerToClient();
        await teardownEntered.Task.WaitAsync(ct);

        // Reserve a second request while the old read loop is paused after publishing Faulted but
        // before it fails its own pending entries. A global id-only sweep would steal this request's
        // completion before it can be written to the replacement connection.
        var survivesReconnect = session.SendAsync(Request(replacementId), ct);
        await replacementSlotReserved.Task.WaitAsync(ct);
        Assert.False(survivesReconnect.IsCompleted);

        releaseTeardown.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => disconnected);

        var inbound = await ReadInboundAsync(replacement.Server, ct);
        Assert.Equal(replacementId, inbound.Id);
        Assert.Equal(replacementNonce, inbound.Nonce);
        await WriteResponseAsync(
            replacement.Server,
            Response(replacementId) with { Nonce = replacementNonce },
            ct);

        var response = await survivesReconnect;
        Assert.True(response.Ok);
        Assert.Equal(replacementId, response.Id);
        Assert.Equal(2, Volatile.Read(ref connectCount));
    }

    // A generic session cannot prove which connect phase produced a timeout. Only the production
    // named-pipe client's explicit AppBridgeConnectFailedException marker may enable fallback.
    [Fact]
    public async Task UnclassifiedConnectFactoryTimeout_IsNotWrappedForFallback()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = cts.Token;
        var inner = new TimeoutException("Timed out connecting to the WinVaultWarden passkey bridge.");
        await using var session = new AppBridgeSession(_ => throw inner);

        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => session.SendAsync(Request("00000000000000000000000000000f0"), ct));
        Assert.Same(inner, thrown);
    }

    public static IEnumerable<object[]> IntegrityFailures()
    {
        yield return [new UnauthorizedAccessException("Peer identity rejected.")];
        yield return [new InvalidDataException("Handshake frame was malformed.")];
        yield return [new IOException("Handshake transport failed.")];
    }

    [Theory]
    [MemberData(nameof(IntegrityFailures))]
    public async Task ConnectFactoryIntegrityFailure_IsNotWrappedForFallback(Exception failure)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var session = new AppBridgeSession(
            _ => Task.FromException<AppBridgeConnection>(failure));

        var thrown = await Record.ExceptionAsync(
            () => session.SendAsync(Request("00000000000000000000000000000f2"), cts.Token));

        Assert.Same(failure, thrown);
        Assert.IsNotType<AppBridgeConnectFailedException>(thrown);
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
