using System.Text;
using Api;
using Core.Enums;
using Core.Models;
using Xunit;

namespace Api.Tests;

public class SignalRMessagePackTests
{
    [Fact]
    public void UpdateType_contains_the_complete_official_wire_range()
    {
        var officialValues = Enum.GetValues<UpdateType>()
            .Where(value => value != UpdateType.None)
            .Select(value => (int)value)
            .Order()
            .ToArray();

        Assert.Equal(Enumerable.Range(0, 28), officialValues);
        Assert.Equal(100, (int)UpdateType.None);
    }

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

    private static byte[] InvocationFrame(
        int argumentPropertyCount,
        Action<List<byte>> writeArgumentProperties)
    {
        var message = new List<byte>();
        Arr(message, 5);
        message.Add(1);
        Map(message, 0);
        message.Add(0xc0);
        Str(message, "ReceiveMessage");
        Arr(message, 1);
        Map(message, argumentPropertyCount);
        writeArgumentProperties(message);
        return FrameOf(message.ToArray());
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

    [Fact]
    public void Parses_every_coalesced_hub_message_in_wire_order()
    {
        var combined = Frame(1, "cipher-first")
            .Concat(new byte[] { 0x02, 0x91, 0x06 })
            .Concat(Frame(7, "folder-second"))
            .ToArray();

        Assert.True(SignalRMessagePack.TryParseInvocations(combined, out var notifications));
        Assert.Collection(
            notifications,
            first =>
            {
                Assert.Equal(1, first.Type);
                Assert.Equal("cipher-first", first.EntityId);
            },
            second =>
            {
                Assert.Equal(7, second.Type);
                Assert.Equal("folder-second", second.EntityId);
            });
    }

    [Fact]
    public void Unknown_invocation_target_is_ignored_without_hiding_notifications()
    {
        var unknownInvocation = new List<byte>();
        Arr(unknownInvocation, 5);
        unknownInvocation.Add(1);
        Map(unknownInvocation, 0);
        unknownInvocation.Add(0xc0);
        Str(unknownInvocation, "UnknownTarget");
        Arr(unknownInvocation, 0);

        var combined = Frame(1, "cipher-first")
            .Concat(FrameOf(unknownInvocation.ToArray()))
            .Concat(Frame(7, "folder-second"))
            .ToArray();

        Assert.True(SignalRMessagePack.TryParseInvocations(combined, out var notifications));
        Assert.Equal(["cipher-first", "folder-second"],
            notifications.Select(notification => notification.EntityId));
    }

    [Fact]
    public void Malformed_unknown_invocation_tail_is_rejected()
    {
        var malformedUnknownInvocation = new List<byte>();
        Arr(malformedUnknownInvocation, 5);
        malformedUnknownInvocation.Add(1);
        Map(malformedUnknownInvocation, 0);
        malformedUnknownInvocation.Add(0xc0);
        Str(malformedUnknownInvocation, "UnknownTarget");
        malformedUnknownInvocation.Add(0);

        var combined = Frame(1, "cipher-first")
            .Concat(FrameOf(malformedUnknownInvocation.ToArray()))
            .ToArray();

        Assert.False(SignalRMessagePack.TryParseInvocations(combined, out var notifications));
        Assert.Empty(notifications);
    }

    [Fact]
    public void Coalesced_message_with_truncated_tail_is_rejected()
    {
        var combined = Frame(1, "cipher-first")
            .Concat(new byte[] { 0x05, 0x91 })
            .ToArray();

        Assert.False(SignalRMessagePack.TryParseInvocations(combined, out var notifications));
        Assert.Empty(notifications);
    }

    [Fact]
    public void Coalesced_message_with_invalid_messagepack_tail_is_rejected()
    {
        var combined = Frame(1, "cipher-first")
            .Concat(new byte[] { 0x01, 0xc1 })
            .ToArray();

        Assert.False(SignalRMessagePack.TryParseInvocations(combined, out var notifications));
        Assert.Empty(notifications);
    }

    [Fact]
    public void Coalesced_message_with_invalid_receive_message_tail_is_rejected()
    {
        var invalidReceiveMessage = InvocationFrame(2, message =>
        {
            Str(message, "ContextId"); message.Add(0xc0);
            Str(message, "Payload"); Map(message, 0);
        });
        var combined = Frame(1, "cipher-first")
            .Concat(invalidReceiveMessage)
            .ToArray();

        Assert.False(SignalRMessagePack.TryParseInvocations(combined, out var notifications));
        Assert.Empty(notifications);
    }

    [Fact]
    public void Invalid_five_byte_length_prefix_is_rejected()
    {
        Assert.False(SignalRMessagePack.TryParseInvocations(
            new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80 },
            out _));
        Assert.False(SignalRMessagePack.TryParseInvocations(
            new byte[] { 0xff, 0xff, 0xff, 0xff, 0x08 },
            out _));
    }

    [Fact]
    public void Frame_larger_than_receive_limit_is_rejected_before_payload_allocation()
    {
        // 1,048,577 encoded as a SignalR little-endian base-128 VarInt.
        var oversizedLengthPrefix = new byte[] { 0x81, 0x80, 0x40 };

        Assert.False(SignalRMessagePack.TryParseInvocations(oversizedLengthPrefix, out _));
    }

    [Fact]
    public void Single_invocation_parser_rejects_a_trailing_second_frame()
    {
        var combined = Frame(1, "cipher-first")
            .Concat(Frame(7, "folder-second"))
            .ToArray();

        Assert.False(SignalRMessagePack.TryParseInvocation(combined, out _));
    }

    [Fact]
    public void Missing_notification_type_is_rejected_instead_of_defaulting_to_cipher_update()
    {
        var frame = InvocationFrame(2, message =>
        {
            Str(message, "ContextId"); message.Add(0xc0);
            Str(message, "Payload");
            Map(message, 1);
            Str(message, "Id"); Str(message, "cipher-1");
        });

        Assert.False(SignalRMessagePack.TryParseInvocation(frame, out _));
    }

    [Fact]
    public void Notification_type_outside_wire_byte_range_is_rejected()
    {
        var frame = InvocationFrame(3, message =>
        {
            Str(message, "ContextId"); message.Add(0xc0);
            Str(message, "Type");
            message.Add(0xcd); message.Add(0x01); message.Add(0x00);
            Str(message, "Payload"); Map(message, 0);
        });

        Assert.False(SignalRMessagePack.TryParseInvocation(frame, out _));
    }

    [Fact]
    public void Parses_organization_collection_settings_and_context()
    {
        var frame = InvocationFrame(3, message =>
        {
            Str(message, "ContextId"); Str(message, "device-context");
            Str(message, "Type"); message.Add(19);
            Str(message, "Payload");
            Map(message, 4);
            Str(message, "OrganizationId"); Str(message, "org-1");
            Str(message, "LimitCollectionCreation"); message.Add(0xc3);
            Str(message, "LimitCollectionDeletion"); message.Add(0xc2);
            Str(message, "LimitItemDeletion"); message.Add(0xc3);
        });

        Assert.True(SignalRMessagePack.TryParseInvocation(frame, out var notification));
        Assert.Equal(19, notification.Type);
        Assert.Equal("device-context", notification.ContextId);
        Assert.Equal("org-1", notification.OrganizationId);
        Assert.True(notification.LimitCollectionCreation);
        Assert.False(notification.LimitCollectionDeletion);
        Assert.True(notification.LimitItemDeletion);
    }

    [Fact]
    public void Parses_cipher_delete_metadata_and_messagepack_timestamp()
    {
        const long seconds = 1_725_000_000;
        const uint nanoseconds = 123_456_700;
        var packed = ((ulong)nanoseconds << 34) | (ulong)seconds;
        var timestamp = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(timestamp, packed);
        var frame = InvocationFrame(3, message =>
        {
            Str(message, "ContextId"); message.Add(0xc0);
            Str(message, "Type"); message.Add(9);
            Str(message, "Payload");
            Map(message, 5);
            Str(message, "Id"); Str(message, "cipher-9");
            Str(message, "UserId"); Str(message, "user-1");
            Str(message, "OrganizationId"); Str(message, "org-1");
            Str(message, "CollectionIds");
            Arr(message, 2); Str(message, "collection-1"); Str(message, "collection-2");
            Str(message, "RevisionDate");
            message.Add(0xd7); message.Add(0xff); message.AddRange(timestamp);
        });

        Assert.True(SignalRMessagePack.TryParseInvocation(frame, out var notification));
        Assert.Equal(9, notification.Type);
        Assert.Equal("cipher-9", notification.EntityId);
        Assert.Equal("user-1", notification.UserId);
        Assert.Equal("org-1", notification.OrganizationId);
        Assert.Equal(["collection-1", "collection-2"], notification.CollectionIds);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanoseconds / 100),
            notification.RevisionDate);
    }

    // ----------------------------------------------------------------
    // S1: DoS 硬化回归
    // ----------------------------------------------------------------

    private static byte[] FrameOf(byte[] payload)
    {
        var outb = new List<byte>();
        var len = payload.Length;
        do
        {
            var x = (byte)(len & 0x7f);
            len >>= 7;
            if (len > 0) x |= 0x80;
            outb.Add(x);
        } while (len > 0);
        outb.AddRange(payload);
        return outb.ToArray();
    }

    private static byte[] Repeat(byte value, int count)
    {
        var a = new byte[count];
        for (int i = 0; i < count; i++) a[i] = value;
        return a;
    }

    [Fact]
    public void ReadValue_DeeplyNested_ThrowsNotStackOverflow()
    {
        // 300 层嵌套 fixarray[1] (0x91) 远超 MaxDepth → 抛可捕获异常而非不可捕获的栈溢出。
        var deep = Repeat(0x91, 300);
        Assert.Throws<System.IO.InvalidDataException>(() =>
        {
            int pos = 0;
            SignalRMessagePack.ReadValue(deep, ref pos);
        });
    }

    [Fact]
    public void TryParseInvocation_DeeplyNested_ReturnsFalseNotCrash()
    {
        var frame = FrameOf(Repeat(0x91, 300));
        Assert.False(SignalRMessagePack.TryParseInvocation(frame, out _));
    }

    [Fact]
    public void ReadValue_HugeArrayCount_ThrowsInsteadOfAllocating()
    {
        // array32 声明约 21 亿元素但缓冲仅几字节 → EnsureCount 抛异常,不预分配巨量数组(内存放大 DoS)。
        var buf = new byte[] { 0xdd, 0x7f, 0xff, 0xff, 0xff };
        Assert.Throws<System.IO.InvalidDataException>(() =>
        {
            int pos = 0;
            SignalRMessagePack.ReadValue(buf, ref pos);
        });
    }

    [Fact]
    public void ReadValue_NegativeMapCount_Throws()
    {
        // map32 count 0xffffffff → (int)-1 → EnsureCount 抛异常。
        var buf = new byte[] { 0xdf, 0xff, 0xff, 0xff, 0xff };
        Assert.Throws<System.IO.InvalidDataException>(() =>
        {
            int pos = 0;
            SignalRMessagePack.ReadValue(buf, ref pos);
        });
    }
}
