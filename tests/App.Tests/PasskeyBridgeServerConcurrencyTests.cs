using System.IO.Pipes;
using System.Threading.Channels;
using App.Services;
using Core.Passkeys;
using Xunit;

namespace App.Tests;

public partial class PasskeyBridgeServerMalformedFrameTests
{
    [Fact]
    public async Task ConnectionSession_RejectsNinthActiveRequestAndReusesReleasedSlot()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var duplex = new InMemoryDuplex();
        var started = 0;
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementId = RequestId(PasskeyBridgeServer.MaxActiveRequestsPerConnection + 2);

        async Task<BrowserPasskeyResponse> HandleAsync(BrowserPasskeyRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref started);
            if (request.Id == replacementId)
                return BrowserPasskeyResponse.Failure(request.Id, PasskeyErrorCodes.ClientNotHandled);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("Unreachable.");
            }
            finally
            {
                if (request.Id == RequestId(1))
                    firstCancelled.TrySetResult();
            }
        }

        var server = new PasskeyBridgeServer(HandleAsync, new AllowAllPeerAuthorizer());
        const string nonce = "test-session-nonce-AAAAAAAAAAAAAAAAAAAAAAAA";
        var connection = server.RunConnectionAsync(duplex.Server, nonce, shutdown.Token);

        for (var ordinal = 1; ordinal <= PasskeyBridgeServer.MaxActiveRequestsPerConnection; ordinal++)
        {
            await WriteRequestAsync(duplex.Client, RequestId(ordinal), nonce, timeout.Token);
        }

        await WaitUntilAsync(
            () => Volatile.Read(ref started) == PasskeyBridgeServer.MaxActiveRequestsPerConnection,
            timeout.Token);

        var rejectedId = RequestId(PasskeyBridgeServer.MaxActiveRequestsPerConnection + 1);
        await WriteRequestAsync(duplex.Client, rejectedId, nonce, timeout.Token);
        var rejected = await BrowserPasskeyMessageProtocol.ReadAsync(
            duplex.Client,
            PasskeyJsonContext.Default.BrowserPasskeyResponse,
            timeout.Token);

        Assert.NotNull(rejected);
        Assert.Equal(rejectedId, rejected!.Id);
        Assert.False(rejected.Ok);
        Assert.Equal(PasskeyErrorCodes.RateLimited, rejected.Error?.Code);
        Assert.Equal(PasskeyBridgeServer.MaxActiveRequestsPerConnection, Volatile.Read(ref started));

        await BrowserPasskeyMessageProtocol.WriteAsync(
            duplex.Client,
            new BrowserPasskeyRequest(RequestId(1), "cancel", Nonce: nonce),
            PasskeyJsonContext.Default.BrowserPasskeyRequest,
            timeout.Token);
        await firstCancelled.Task.WaitAsync(timeout.Token);

        BrowserPasskeyResponse? replacement;
        do
        {
            await WriteRequestAsync(duplex.Client, replacementId, nonce, timeout.Token);
            replacement = await BrowserPasskeyMessageProtocol.ReadAsync(
                duplex.Client,
                PasskeyJsonContext.Default.BrowserPasskeyResponse,
                timeout.Token);

            Assert.NotNull(replacement);
            Assert.Equal(replacementId, replacement!.Id);
            Assert.False(replacement.Ok);
            Assert.True(replacement.Error?.Code is
                PasskeyErrorCodes.RateLimited or PasskeyErrorCodes.ClientNotHandled);
            if (replacement.Error?.Code == PasskeyErrorCodes.RateLimited)
                await Task.Yield();
        }
        while (replacement.Error?.Code == PasskeyErrorCodes.RateLimited);

        Assert.Equal(
            PasskeyBridgeServer.MaxActiveRequestsPerConnection + 1,
            Volatile.Read(ref started));

        shutdown.Cancel();
        await connection.WaitAsync(timeout.Token);
        await server.DisposeAsync();
    }

    [Fact]
    public async Task CompletedOrFaultedListener_IsNotRunningAndStartRecovers()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        Task ListenAsync(string _, CancellationToken ct)
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                firstStarted.TrySetResult();
                return Task.FromException(new IOException("Synthetic listener failure."));
            }

            secondStarted.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        var server = new PasskeyBridgeServer(
            (request, _) => Task.FromResult(
                BrowserPasskeyResponse.Failure(request.Id!, PasskeyErrorCodes.ClientNotHandled)),
            new AllowAllPeerAuthorizer(),
            ListenAsync);

        server.Start();
        await firstStarted.Task.WaitAsync(timeout.Token);
        await WaitUntilAsync(() => !server.IsRunning, timeout.Token);

        server.Start();
        await secondStarted.Task.WaitAsync(timeout.Token);
        Assert.True(server.IsRunning);

        await server.StopAsync();
        Assert.False(server.IsRunning);
        await server.DisposeAsync();
    }

    [Fact]
    public async Task FaultedListener_StopObservesFailureAndConverges()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listenerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task ListenAsync(string _, CancellationToken __)
        {
            listenerStarted.TrySetResult();
            return Task.FromException(new InvalidDataException("Synthetic listener failure."));
        }

        var server = new PasskeyBridgeServer(
            (request, _) => Task.FromResult(
                BrowserPasskeyResponse.Failure(request.Id!, PasskeyErrorCodes.ClientNotHandled)),
            new AllowAllPeerAuthorizer(),
            ListenAsync);

        server.Start();
        await listenerStarted.Task.WaitAsync(timeout.Token);
        await WaitUntilAsync(() => !server.IsRunning, timeout.Token);

        var stopFailure = await Record.ExceptionAsync(server.StopAsync);
        Assert.Null(stopFailure);
        Assert.False(server.IsRunning);

        await server.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_IsSharedWaitsForActiveStopAndPermanentlyClosesLifecycle()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listenerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task ListenAsync(string _, CancellationToken ct)
        {
            using var registration = ct.Register(() => cancellationObserved.TrySetResult());
            listenerStarted.TrySetResult();
            await allowDrain.Task;
        }

        var server = new PasskeyBridgeServer(
            (request, _) => Task.FromResult(
                BrowserPasskeyResponse.Failure(request.Id!, PasskeyErrorCodes.ClientNotHandled)),
            new AllowAllPeerAuthorizer(),
            ListenAsync);
        var slots = (SemaphoreSlim)typeof(PasskeyBridgeServer)
            .GetField("_connectionSlots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(server)!;

        server.Start();
        await listenerStarted.Task.WaitAsync(timeout.Token);

        var stop = server.StopAsync();
        await cancellationObserved.Task.WaitAsync(timeout.Token);
        var firstDispose = server.DisposeAsync().AsTask();
        var secondDispose = server.DisposeAsync().AsTask();

        Assert.Same(firstDispose, secondDispose);
        Assert.False(firstDispose.IsCompleted);

        allowDrain.TrySetResult();
        await Task.WhenAll(stop, firstDispose).WaitAsync(timeout.Token);

        Assert.False(server.IsRunning);
        Assert.Throws<ObjectDisposedException>(() => server.Start());
        Assert.Throws<ObjectDisposedException>(() => slots.Wait(0));
        await server.StopAsync();
    }

    [Fact]
    public async Task FifthConcurrentConnection_IsClosedAndShutdownReleasesAllSlots()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new PasskeyBridgeServer(
            (request, _) => Task.FromResult(
                BrowserPasskeyResponse.Failure(request.Id!, PasskeyErrorCodes.ClientNotHandled)),
            new AllowAllPeerAuthorizer());
        var clients = new List<ConnectedClient>();
        server.Start();

        try
        {
            for (var i = 0; i < PasskeyBridgeServer.MaxConcurrentConnections; i++)
                clients.Add(await ConnectAsync());

            var rejected = await ConnectExpectingRejectionAsync(timeout.Token);
            Assert.True(rejected is UnauthorizedAccessException or IOException or EndOfStreamException);

            await clients[0].DisposeAsync();
            clients.RemoveAt(0);

            ConnectedClient? replacement = null;
            while (replacement is null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    replacement = await ConnectAsync();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or EndOfStreamException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
                }
            }
            clients.Add(replacement);
        }
        finally
        {
            foreach (var client in clients)
                await client.DisposeAsync();
            await server.StopAsync();
        }

        server.Start();
        try
        {
            await using var afterRestart = await ConnectAsync();
            Assert.True(server.IsRunning);
        }
        finally
        {
            await server.StopAsync();
        }

        await server.DisposeAsync();
    }

    private static Task WriteRequestAsync(Stream stream, string id, string nonce, CancellationToken ct) =>
        BrowserPasskeyMessageProtocol.WriteAsync(
            stream,
            new BrowserPasskeyRequest(id, "passkey.get", Nonce: nonce),
            PasskeyJsonContext.Default.BrowserPasskeyRequest,
            ct);

    private static string RequestId(int ordinal) => ordinal.ToString("x32");

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate())
            await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
    }

    private static async Task<Exception> ConnectExpectingRejectionAsync(CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", BrowserPasskeyBridge.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(ct);
        var authorizer = new AllowAllPeerAuthorizer();

        return await Record.ExceptionAsync(() => PasskeyBridgeHandshake.CompleteClientAsync(
                pipe,
                (uint)Environment.ProcessId,
                innerCt => authorizer.AuthorizeServerAsync(pipe, string.Empty, innerCt),
                ct))
            ?? new InvalidOperationException("The excess connection unexpectedly completed its handshake.");
    }

    private sealed class InMemoryDuplex : IAsyncDisposable
    {
        private readonly Channel<byte[]> _clientToServer = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _serverToClient = Channel.CreateUnbounded<byte[]>();

        public InMemoryDuplex()
        {
            Client = new ChannelStream(_serverToClient.Reader, _clientToServer.Writer);
            Server = new ChannelStream(_clientToServer.Reader, _serverToClient.Writer);
        }

        public Stream Client { get; }
        public Stream Server { get; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private sealed class ChannelStream(
        ChannelReader<byte[]> reader,
        ChannelWriter<byte[]> writer) : Stream
    {
        private byte[]? _current;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_current is null || _offset == _current.Length)
            {
                if (!await reader.WaitToReadAsync(ct))
                    return 0;
                if (!reader.TryRead(out _current))
                    continue;
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await writer.WriteAsync(buffer.ToArray(), ct);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                writer.TryComplete();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
