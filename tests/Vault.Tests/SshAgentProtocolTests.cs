using System.Buffers.Binary;
using Core.SshAgent;
using Xunit;

namespace Vault.Tests;

public sealed class SshAgentProtocolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1_048_577)]
    [InlineData(uint.MaxValue)]
    public async Task ReadPacketRejectsInvalidLengthBeforeBodyAllocation(uint length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => SshAgentProtocol.ReadPacketAsync(stream));
    }

    [Fact]
    public async Task ReadPacketTreatsCleanEofAndTruncationDifferently()
    {
        using var empty = new MemoryStream();
        Assert.Null(await SshAgentProtocol.ReadPacketAsync(empty));
        using var shortHeader = new MemoryStream([0, 0]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => SshAgentProtocol.ReadPacketAsync(shortHeader));
        using var shortBody = new MemoryStream([0, 0, 0, 2, 11]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => SshAgentProtocol.ReadPacketAsync(shortBody));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(25)]
    [InlineData(255)]
    public async Task UnsupportedMutationAndUnknownRequestsFailClosed(byte type)
    {
        using var client = new Client();
        var response = await SshAgentProtocol.HandlePacketAsync([type], new Handler(), client);
        Assert.Equal(new byte[] { SshAgentProtocol.Failure }, response);
    }

    [Fact]
    public async Task ExtensionsAreNotAdvertisedAsAuthenticatedSessionBindings()
    {
        using var client = new Client();
        var response = await SshAgentProtocol.HandlePacketAsync([27], new Handler(), client);
        Assert.Equal(new byte[] { SshAgentProtocol.Failure }, response);
    }

    [Fact]
    public async Task IdentityAnswerAndSignatureUseLengthPrefixedSshWireEncoding()
    {
        using var client = new Client();
        var handler = new Handler { Identities = [new([1, 2], "Key")] };
        var identities = await SshAgentProtocol.HandlePacketAsync([11], handler, client);
        Assert.Equal((byte)12, identities[0]);
        var reader = new SshAgentReader(identities.AsSpan(1));
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(new byte[] { 1, 2 }, reader.ReadString(10).ToArray());
        Assert.Equal("Key"u8.ToArray(), reader.ReadString(10).ToArray());
        reader.RequireEnd();

        var response = await SshAgentProtocol.HandlePacketAsync(SignPacket(), handler, client);
        Assert.Equal(new byte[] { 14, 0, 0, 0, 2, 4, 5 }, response);
        Assert.Equal(new byte[] { 1, 2 }, handler.Key);
        Assert.Equal(new byte[] { 3 }, handler.Data);
        Assert.Equal(4u, handler.Flags);
    }

    [Fact]
    public async Task MalformedAndTrailingSignFieldsNeverReachHandler()
    {
        using var client = new Client();
        var handler = new Handler();
        var truncated = await SshAgentProtocol.HandlePacketAsync([13, 0, 0, 0, 3, 1], handler, client);
        var trailing = await SshAgentProtocol.HandlePacketAsync([.. SignPacket(), 99], handler, client);
        var identities = await SshAgentProtocol.HandlePacketAsync([11, 99], handler, client);
        Assert.Equal(new byte[] { 5 }, truncated);
        Assert.Equal(new byte[] { 5 }, trailing);
        Assert.Equal(new byte[] { 5 }, identities);
        Assert.Equal(0, handler.SignCalls);
    }

    [Fact]
    public async Task LostProcessIdentityPreventsSignatureResponse()
    {
        using var client = new Client();
        var handler = new Handler { BeforeSignReturns = () => client.Current = false };
        var response = await SshAgentProtocol.HandlePacketAsync(SignPacket(), handler, client);
        Assert.Equal(new byte[] { 5 }, response);
    }

    [Fact]
    public async Task IdentityCommentsCannotInjectTerminalControls()
    {
        using var client = new Client();
        var handler = new Handler { Identities = [new([1], "key\u001b[0m\u202e")] };
        var response = await SshAgentProtocol.HandlePacketAsync([11], handler, client);
        var reader = new SshAgentReader(response.AsSpan(1));
        Assert.Equal(1u, reader.ReadUInt32());
        reader.ReadString(10);
        var comment = System.Text.Encoding.UTF8.GetString(reader.ReadString(100));
        Assert.DoesNotContain('\u001b', comment);
        Assert.DoesNotContain('\u202e', comment);
        Assert.Contains('\uFFFD', comment);
    }

    [Fact]
    public async Task LargeIdentityReplyFitsOpenSshReplyBudgetAndReportsActualCount()
    {
        using var client = new Client();
        var handler = new Handler
        {
            Identities = Enumerable.Range(0, 256)
                .Select(_ => new SshAgentIdentity(new byte[SshAgentProtocol.MaximumPublicKeyLength], new string('\u00e9', 256)))
                .ToArray(),
        };
        var response = await SshAgentProtocol.HandlePacketAsync([11], handler, client);
        Assert.True(response.Length <= SshAgentProtocol.MaximumResponseLength);
        Assert.Equal((byte)12, response[0]);
        var reader = new SshAgentReader(response.AsSpan(1));
        var count = reader.ReadUInt32();
        Assert.InRange(count, 1u, 255u);
        for (var index = 0; index < count; index++)
        {
            Assert.Equal(SshAgentProtocol.MaximumPublicKeyLength,
                reader.ReadString(SshAgentProtocol.MaximumPublicKeyLength).Length);
            Assert.Equal(512, reader.ReadString(1_024).Length);
        }
        reader.RequireEnd();
    }

    [Fact]
    public async Task ServeHandlesMultipleRequestsAndStopsAtEof()
    {
        using var input = new MemoryStream();
        await SshAgentProtocol.WritePacketAsync(input, [11]);
        await SshAgentProtocol.WritePacketAsync(input, [17]);
        using var duplex = new Duplex(input.ToArray());
        using var client = new Client();
        await SshAgentProtocol.ServeAsync(duplex, new Handler(), client);
        duplex.Output.Position = 0;
        Assert.Equal(new byte[] { 12, 0, 0, 0, 0 }, await SshAgentProtocol.ReadPacketAsync(duplex.Output));
        Assert.Equal(new byte[] { 5 }, await SshAgentProtocol.ReadPacketAsync(duplex.Output));
        Assert.Null(await SshAgentProtocol.ReadPacketAsync(duplex.Output));
    }

    private static byte[] SignPacket()
    {
        using var packet = new MemoryStream();
        packet.WriteByte(13);
        SshAgentProtocol.WriteString(packet, [1, 2]);
        SshAgentProtocol.WriteString(packet, [3]);
        SshAgentProtocol.WriteUInt32(packet, 4);
        return packet.ToArray();
    }

    private sealed class Client : ISshAgentClientIdentity
    {
        public bool Current { get; set; } = true;
        public SshAgentClientInfo Info => new(42, "C:\\Windows\\System32\\OpenSSH\\ssh.exe", DateTimeOffset.UnixEpoch);
        public bool IsCurrent => Current;
        public void Dispose() => Current = false;
    }

    private sealed class Handler : ISshAgentRequestHandler
    {
        public IReadOnlyList<SshAgentIdentity> Identities { get; set; } = [];
        public byte[]? Key { get; private set; }
        public byte[]? Data { get; private set; }
        public uint Flags { get; private set; }
        public int SignCalls { get; private set; }
        public Action? BeforeSignReturns { get; init; }

        public Task<IReadOnlyList<SshAgentIdentity>> GetIdentitiesAsync(ISshAgentClientIdentity client, CancellationToken ct = default) =>
            Task.FromResult(Identities);

        public Task<byte[]?> SignAsync(ISshAgentClientIdentity client, byte[] publicKeyBlob, byte[] data, uint flags, CancellationToken ct = default)
        {
            SignCalls++;
            Key = publicKeyBlob.ToArray();
            Data = data.ToArray();
            Flags = flags;
            BeforeSignReturns?.Invoke();
            return Task.FromResult<byte[]?>([4, 5]);
        }
    }

    private sealed class Duplex(byte[] input) : Stream
    {
        private readonly MemoryStream _input = new(input);
        public MemoryStream Output { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Output.WriteAsync(buffer, cancellationToken);
        public override void Flush() => Output.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _input.Dispose(); Output.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
