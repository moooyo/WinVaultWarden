using System.Text;
using Api;
using Core.Models;
using Xunit;

namespace Api.Tests;

public class SignalRMessagePackTests
{
    // ----- minimal msgpack helpers (test-only) -----

    private static void Str(List<byte> b, string s)
    {
        var u = Encoding.UTF8.GetBytes(s);
        if (u.Length < 32)
        {
            b.Add((byte)(0xa0 | u.Length));
        }
        else
        {
            // str8
            b.Add(0xd9);
            b.Add((byte)u.Length);
        }
        b.AddRange(u);
    }

    private static void Arr(List<byte> b, int n) => b.Add((byte)(0x90 | n));
    private static void Map(List<byte> b, int n) => b.Add((byte)(0x80 | n));

    /// <summary>
    /// Builds a SignalR MessagePack frame:
    ///   [1, {}, null, "ReceiveMessage", [{"ContextId":null,"Type":type,"Payload":{"Id":id,"UserId":"u"}}]]
    /// with a 7-bit varint length prefix.
    /// </summary>
    private static byte[] Frame(int type, string id)
    {
        var m = new List<byte>();
        Arr(m, 5);
        m.Add(1);            // [0] = 1  (invocation)
        Map(m, 0);           // [1] = {} (headers)
        m.Add(0xc0);         // [2] = null
        Str(m, "ReceiveMessage"); // [3]
        Arr(m, 1);           // [4] = args array (length 1)
        Map(m, 3);           //   args[0] = map{3}
        Str(m, "ContextId"); m.Add(0xc0);
        Str(m, "Type");      m.Add((byte)type); // positive fixint
        Str(m, "Payload");
        Map(m, 2);
        Str(m, "Id");        Str(m, id);
        Str(m, "UserId");    Str(m, "u");

        // 7-bit varint length prefix
        var len = m.Count;
        var outb = new List<byte>();
        do
        {
            var x = (byte)(len & 0x7f);
            len >>= 7;
            if (len > 0) x |= 0x80;
            outb.Add(x);
        } while (len > 0);

        outb.AddRange(m);
        return outb.ToArray();
    }

    // ----------------------------------------------------------------
    // Tests from the brief
    // ----------------------------------------------------------------

    [Fact]
    public void Parses_folder_create_invocation()
    {
        Assert.True(SignalRMessagePack.TryParseInvocation(Frame(7, "folder-123"), out var msg));
        Assert.Equal(7, msg.Type);
        Assert.Equal("folder-123", msg.EntityId);
    }

    [Fact]
    public void Ignores_app_ping_frame()
    {
        // [6] with varint prefix (0x02 = length 2, then 0x91 0x06 = fixarray[1] containing fixint 6)
        var ping = new byte[] { 0x02, 0x91, 0x06 };
        Assert.False(SignalRMessagePack.TryParseInvocation(ping, out _));
    }

    [Fact]
    public void Handshake_bytes_are_messagepack_json_without_record_separator()
    {
        Assert.Equal(
            Encoding.UTF8.GetBytes("{\"protocol\":\"messagepack\",\"version\":1}"),
            SignalRMessagePack.HandshakeBytes);
    }

    // ----------------------------------------------------------------
    // Extra tests (beyond the brief)
    // ----------------------------------------------------------------

    /// <summary>
    /// Cipher-create (Type=1) with a str8-encoded id (>31 bytes) exercises both
    /// the str8 decoder path and the map decode round-trip.
    /// </summary>
    [Fact]
    public void Parses_cipher_create_invocation_with_str8_id()
    {
        // 32-character id → forces the str8 branch in the Str() helper
        var longId = "cipher-id-exactly-32-bytes-long!"; // exactly 32 chars
        Assert.Equal(32, Encoding.UTF8.GetByteCount(longId));

        Assert.True(SignalRMessagePack.TryParseInvocation(Frame(1, longId), out var msg));
        Assert.Equal(1, msg.Type);
        Assert.Equal(longId, msg.EntityId);
    }

    /// <summary>
    /// A map16 outer container: embed the args map in a map16 (0xde) instead of
    /// a fixmap, proving ReadValue handles map16 correctly.
    /// </summary>
    [Fact]
    public void Parses_invocation_with_map16_args_dict()
    {
        // Build body with map16 (2-byte length header) for the args[0] map
        var m = new List<byte>();
        Arr(m, 5);
        m.Add(1);
        Map(m, 0);
        m.Add(0xc0);
        Str(m, "ReceiveMessage");
        Arr(m, 1);

        // map16 { "ContextId":null, "Type":7, "Payload":{...} }
        m.Add(0xde);
        m.Add(0x00);
        m.Add(0x03); // 3 entries as big-endian uint16
        Str(m, "ContextId"); m.Add(0xc0);
        Str(m, "Type");      m.Add(0x07); // fixint 7
        Str(m, "Payload");
        Map(m, 2);
        Str(m, "Id");     Str(m, "folder-456");
        Str(m, "UserId"); Str(m, "u2");

        var len = m.Count;
        var outb = new List<byte>();
        do
        {
            var x = (byte)(len & 0x7f);
            len >>= 7;
            if (len > 0) x |= 0x80;
            outb.Add(x);
        } while (len > 0);
        outb.AddRange(m);

        Assert.True(SignalRMessagePack.TryParseInvocation(outb.ToArray(), out var msg));
        Assert.Equal(7, msg.Type);
        Assert.Equal("folder-456", msg.EntityId);
    }

    /// <summary>
    /// Completely malformed/truncated byte sequence → returns false without throwing.
    /// </summary>
    [Fact]
    public void Returns_false_for_truncated_frame()
    {
        Assert.False(SignalRMessagePack.TryParseInvocation(new byte[] { 0x10, 0x92, 0x01 }, out _));
    }

    /// <summary>
    /// Empty span → returns false without throwing.
    /// </summary>
    [Fact]
    public void Returns_false_for_empty_span()
    {
        Assert.False(SignalRMessagePack.TryParseInvocation(ReadOnlySpan<byte>.Empty, out _));
    }
}
