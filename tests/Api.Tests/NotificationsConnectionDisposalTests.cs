using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Api;
using Xunit;

namespace Api.Tests;

public sealed class NotificationsConnectionDisposalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposal_after_protocol_failure_is_bounded_and_shared_by_concurrent_callers(
        bool acknowledgeClose)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var session = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var closeReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePeer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = ServePeerAsync(
            listener, closeReceived, releasePeer, acknowledgeClose, deadline.Token);
        var connection = new NotificationsConnection(TimeSpan.FromMilliseconds(100));
        Task? disposal = null;

        try
        {
            await connection.ConnectAsync(
                $"http://127.0.0.1:{port}/notifications", "synthetic-token", deadline.Token);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await foreach (var _ in connection.ReadAsync(session.Token)) { }
            });

            disposal = connection.DisposeAsync().AsTask();
            var concurrentDisposal = connection.DisposeAsync().AsTask();
            Assert.Same(disposal, concurrentDisposal);
            await closeReceived.Task.WaitAsync(deadline.Token);
            await session.CancelAsync();
            await Task.WhenAll(disposal, concurrentDisposal)
                .WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);

            // The peer still owns an open TCP connection. Completion cannot depend on it
            // dropping the transport, even when it deliberately withholds the close reply.
            Assert.False(peer.IsCompleted);
            await connection.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.ConnectAsync(
                $"http://127.0.0.1:{port}/notifications", "synthetic-token", deadline.Token));
        }
        finally
        {
            disposal ??= connection.DisposeAsync().AsTask();
            releasePeer.TrySetResult();
            listener.Stop();
            await Task.WhenAll(peer, disposal)
                .WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
        }
    }

    private static async Task ServePeerAsync(
        TcpListener listener,
        TaskCompletionSource closeReceived,
        TaskCompletionSource releasePeer,
        bool acknowledgeClose,
        CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        await using var stream = client.GetStream();
        var headers = new List<byte>();
        var single = new byte[1];
        while (headers.Count < 16 * 1024)
        {
            await stream.ReadExactlyAsync(single, ct);
            headers.Add(single[0]);
            if (headers.Count >= 4
                && headers[^4] == 13 && headers[^3] == 10
                && headers[^2] == 13 && headers[^1] == 10)
                break;
        }

        var request = Encoding.ASCII.GetString(headers.ToArray());
        var key = request.Split("\r\n")
            .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            .Split(':', 2)[1].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
            key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\nConnection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n"), ct);

        var handshake = await ReadFrameAsync(stream, ct);
        Assert.Equal(1, handshake.Opcode);
        Assert.Equal(
            Encoding.UTF8.GetString(SignalRMessagePack.HandshakeBytes) + "\u001e",
            Encoding.UTF8.GetString(handshake.Payload));
        await stream.WriteAsync(new byte[] { 0x81, 0x03, 0x7b, 0x7d, 0x1e }, ct);
        // An incomplete SignalR varint fails message parsing while the WebSocket is open.
        await stream.WriteAsync(new byte[] { 0x82, 0x01, 0x80 }, ct);

        var close = await ReadFrameAsync(stream, ct);
        Assert.Equal(8, close.Opcode);
        closeReceived.TrySetResult();
        if (acknowledgeClose)
        {
            await stream.WriteAsync(new byte[] { 0x88, (byte)close.Payload.Length }, ct);
            await stream.WriteAsync(close.Payload, ct);
        }
        await releasePeer.Task.WaitAsync(ct);
    }

    private static async Task<(int Opcode, byte[] Payload)> ReadFrameAsync(
        NetworkStream stream,
        CancellationToken ct)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, ct);
        var length = (int)(header[1] & 0x7f);
        if (length == 126)
        {
            var extended = new byte[2];
            await stream.ReadExactlyAsync(extended, ct);
            length = (extended[0] << 8) | extended[1];
        }
        else if (length == 127)
        {
            throw new InvalidDataException("Unexpected oversized test frame.");
        }

        var mask = new byte[4];
        if ((header[1] & 0x80) != 0)
            await stream.ReadExactlyAsync(mask, ct);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        if ((header[1] & 0x80) != 0)
            for (var index = 0; index < payload.Length; index++)
                payload[index] ^= mask[index % 4];
        return (header[0] & 0x0f, payload);
    }
}
