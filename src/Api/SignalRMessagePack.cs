using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Core.Models;

namespace Api;

/// <summary>
/// Minimal hand-rolled MessagePack decoder and SignalR invocation parser.
/// AOT-safe: zero NuGet, zero reflection at runtime.
///
/// Supports decoding every MessagePack type so positions remain aligned even
/// for types this layer does not use (ext, float, bin → returned as byte[] or null).
/// </summary>
public static class SignalRMessagePack
{
    /// <summary>
    /// Maximum size of one complete WebSocket binary message accepted from the hub.
    /// This bounds both the accumulated transport message and every framed HubMessage.
    /// </summary>
    public const int MaxReceiveMessageSize = 1 * 1024 * 1024;

    private const int MaxLengthPrefixSize = 5;

    // ----------------------------------------------------------------
    // Public surface
    // ----------------------------------------------------------------

    /// <summary>
    /// The handshake payload to send on connection open.
    /// Note: the 0x1e record separator is appended by the connection layer (Task 3).
    /// </summary>
    public static readonly byte[] HandshakeBytes =
        Encoding.UTF8.GetBytes("{\"protocol\":\"messagepack\",\"version\":1}");

    /// <summary>
    /// Reads a 7-bit varint length prefix, then decodes the MessagePack value.
    /// Returns true if the frame is a SignalR invocation of "ReceiveMessage"
    /// (outer array[0]==1L and [3]=="ReceiveMessage") and populates <paramref name="msg"/>.
    /// Returns false (without throwing) for pings, malformed data, or non-invocation messages.
    /// </summary>
    public static bool TryParseInvocation(ReadOnlySpan<byte> frame, out NotificationMessage msg)
    {
        msg = default!;
        if (!TryReadFrame(frame, out var prefixLength, out var payloadLength)
            || prefixLength + payloadLength != frame.Length)
            return false;

        return TryParseInvocationPayload(
            frame.Slice(prefixLength, payloadLength),
            out msg,
            out _);
    }

    /// <summary>
    /// Splits one complete WebSocket binary message into SignalR length-prefixed
    /// HubMessages and returns all matching notification invocations in wire order.
    /// Returns false when any frame is incomplete, malformed, exceeds
    /// <see cref="MaxReceiveMessageSize"/>, or violates the ReceiveMessage contract.
    /// </summary>
    public static bool TryParseInvocations(
        ReadOnlySpan<byte> message,
        out IReadOnlyList<NotificationMessage> notifications)
    {
        notifications = Array.Empty<NotificationMessage>();
        if (message.Length > MaxReceiveMessageSize)
            return false;

        List<NotificationMessage>? parsed = null;
        var position = 0;
        while (position < message.Length)
        {
            var remaining = message[position..];
            if (!TryReadFrame(remaining, out var prefixLength, out var payloadLength))
                return false;

            var payload = remaining.Slice(prefixLength, payloadLength);
            if (TryParseInvocationPayload(
                    payload,
                    out var notification,
                    out var validMessage))
            {
                parsed ??= new List<NotificationMessage>();
                parsed.Add(notification);
            }
            else if (!validMessage)
            {
                return false;
            }

            position += prefixLength + payloadLength;
        }

        if (parsed is not null)
            notifications = parsed;
        return true;
    }

    private static bool TryReadFrame(
        ReadOnlySpan<byte> source,
        out int prefixLength,
        out int payloadLength)
    {
        prefixLength = 0;
        payloadLength = 0;
        if (source.IsEmpty)
            return false;

        uint length = 0;
        byte byteRead = 0;
        var availablePrefixBytes = Math.Min(MaxLengthPrefixSize, source.Length);
        while (prefixLength < availablePrefixBytes)
        {
            byteRead = source[prefixLength];
            length |= (uint)(byteRead & 0x7f) << (prefixLength * 7);
            prefixLength++;
            if ((byteRead & 0x80) == 0)
                break;
        }

        if ((byteRead & 0x80) != 0)
            return false;
        if (prefixLength == MaxLengthPrefixSize && byteRead > 7)
            return false;
        if (length > MaxReceiveMessageSize)
            return false;
        if ((ulong)prefixLength + length > (ulong)source.Length)
            return false;

        payloadLength = (int)length;
        return true;
    }

    private static bool TryParseInvocationPayload(
        ReadOnlySpan<byte> payload,
        out NotificationMessage msg,
        out bool validMessage)
    {
        msg = default!;
        validMessage = false;
        try
        {
            // Decode exactly one MessagePack value from the framed payload.
            int ppos = 0;
            var value = ReadValue(payload, ref ppos);
            if (ppos != payload.Length) return false;
            validMessage = true;

            // Non-invocation HubMessages such as ping remain safe to ignore.
            if (value is not object?[] arr) return false;
            if (arr.Length == 0) return false;
            if (arr[0] is not long msgType || msgType != 1L) return false;

            // A type-1 invocation must be structurally complete. Invocations for other
            // targets are valid but irrelevant to this client; a malformed or invalid
            // ReceiveMessage must fail the entire coalesced transport message.
            validMessage = false;
            if (arr.Length < 5) return false;
            if (arr[3] is not string methodName) return false;
            if (arr[4] is not object?[] args) return false;
            if (methodName != "ReceiveMessage")
            {
                validMessage = true;
                return false;
            }

            // Unpack args.
            if (args.Length < 1) return false;
            if (args[0] is not Dictionary<string, object?> argsDict) return false;

            if (!argsDict.TryGetValue("Type", out var typeVal)
                || typeVal is not long rawType
                || rawType is < byte.MinValue or > byte.MaxValue)
                return false;
            if (!argsDict.TryGetValue("Payload", out var payloadVal)
                || payloadVal is not Dictionary<string, object?> payloadDict)
                return false;
            if (!TryReadOptionalString(argsDict, "ContextId", out var contextId)
                || !TryReadOptionalString(payloadDict, "Id", out var entityId)
                || !TryReadOptionalString(payloadDict, "UserId", out var userId)
                || !TryReadOptionalString(payloadDict, "OrganizationId", out var organizationId)
                || !TryReadOptionalStringArray(payloadDict, "CollectionIds", out var collectionIds)
                || !TryReadOptionalTimestamp(payloadDict, "RevisionDate", out var revisionDate)
                || !TryReadOptionalTimestamp(payloadDict, "Date", out var date)
                || !TryReadOptionalBoolean(payloadDict, "Enabled", out var enabled)
                || !TryReadOptionalBoolean(
                    payloadDict,
                    "LimitCollectionCreation",
                    out var limitCollectionCreation)
                || !TryReadOptionalBoolean(
                    payloadDict,
                    "LimitCollectionDeletion",
                    out var limitCollectionDeletion)
                || !TryReadOptionalBoolean(
                    payloadDict,
                    "LimitItemDeletion",
                    out var limitItemDeletion)
                || !TryReadOptionalInt32(payloadDict, "Reason", out var logoutReason))
                return false;

            msg = new NotificationMessage(
                (int)rawType,
                entityId,
                contextId,
                userId,
                organizationId,
                collectionIds,
                revisionDate,
                date,
                enabled,
                limitCollectionCreation,
                limitCollectionDeletion,
                limitItemDeletion,
                logoutReason);
            validMessage = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ----------------------------------------------------------------
    // Core decoder — returns object? following the mapping:
    //   map   → Dictionary<string, object?>
    //   array → object?[]
    //   int   → long
    //   str   → string
    //   bool  → bool
    //   nil   → null
    //   float → double (f32 promoted)
    //   bin   → byte[]
    //   ext   → byte[] (type byte prepended, then data)
    // ----------------------------------------------------------------

    /// <summary>
    /// Reads one MessagePack value from <paramref name="buf"/> at position <paramref name="pos"/>,
    /// advances pos past the consumed bytes, and returns the decoded value.
    /// </summary>
    public static object? ReadValue(ReadOnlySpan<byte> buf, ref int pos) => ReadValue(buf, ref pos, 0);

    // 嵌套容器最大层级。防止恶意/中间人服务端发深层嵌套帧(如连续 0x91 fixarray[1])触发
    // **不可捕获的** StackOverflowException 使整进程崩溃(远程 DoS)。合法 ReceiveMessage 帧深度仅个位数。
    private const int MaxDepth = 64;

    private static object? ReadValue(ReadOnlySpan<byte> buf, ref int pos, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException("MessagePack: nesting exceeds max depth");
        if (pos >= buf.Length)
            throw new InvalidDataException("Unexpected end of MessagePack data");

        byte first = buf[pos++];

        // ---- positive fixint (0x00–0x7f) ----
        if (first <= 0x7f)
            return (long)first;

        // ---- fixmap (0x80–0x8f) ----
        if (first >= 0x80 && first <= 0x8f)
            return ReadMap(buf, ref pos, first & 0x0f, depth);

        // ---- fixarray (0x90–0x9f) ----
        if (first >= 0x90 && first <= 0x9f)
            return ReadArray(buf, ref pos, first & 0x0f, depth);

        // ---- fixstr (0xa0–0xbf) ----
        if (first >= 0xa0 && first <= 0xbf)
            return ReadStr(buf, ref pos, first & 0x1f);

        // ---- nil ----
        if (first == 0xc0) return null;

        // 0xc1 is never-used; treat as error but consume nothing extra
        if (first == 0xc1)
            throw new InvalidDataException("MessagePack: unused format 0xc1");

        // ---- false / true ----
        if (first == 0xc2) return false;
        if (first == 0xc3) return true;

        // ---- bin 8 / 16 / 32 ----
        if (first == 0xc4) return ReadBin(buf, ref pos, ReadUInt8(buf, ref pos));
        if (first == 0xc5) return ReadBin(buf, ref pos, ReadUInt16BE(buf, ref pos));
        if (first == 0xc6) return ReadBin(buf, ref pos, (int)ReadUInt32BE(buf, ref pos));

        // ---- ext 8 / 16 / 32 ----
        if (first == 0xc7) return ReadExt(buf, ref pos, ReadUInt8(buf, ref pos));
        if (first == 0xc8) return ReadExt(buf, ref pos, ReadUInt16BE(buf, ref pos));
        if (first == 0xc9) return ReadExt(buf, ref pos, (int)ReadUInt32BE(buf, ref pos));

        // ---- float 32 (return as double) ----
        if (first == 0xca)
        {
            Need(buf, pos, 4);
            float f = BinaryPrimitives.ReadSingleBigEndian(buf.Slice(pos, 4));
            pos += 4;
            return (double)f;
        }

        // ---- float 64 ----
        if (first == 0xcb)
        {
            Need(buf, pos, 8);
            double d = BinaryPrimitives.ReadDoubleBigEndian(buf.Slice(pos, 8));
            pos += 8;
            return d;
        }

        // ---- uint 8 / 16 / 32 / 64 ----
        if (first == 0xcc) return (long)(ulong)ReadUInt8(buf, ref pos);
        if (first == 0xcd) return (long)(ulong)ReadUInt16BE(buf, ref pos);
        if (first == 0xce) return (long)(ulong)ReadUInt32BE(buf, ref pos);
        if (first == 0xcf)
        {
            Need(buf, pos, 8);
            ulong u64 = BinaryPrimitives.ReadUInt64BigEndian(buf.Slice(pos, 8));
            pos += 8;
            return (long)u64;
        }

        // ---- int 8 / 16 / 32 / 64 ----
        if (first == 0xd0) return (long)(sbyte)ReadUInt8(buf, ref pos);
        if (first == 0xd1)
        {
            Need(buf, pos, 2);
            short s16 = BinaryPrimitives.ReadInt16BigEndian(buf.Slice(pos, 2));
            pos += 2;
            return (long)s16;
        }
        if (first == 0xd2)
        {
            Need(buf, pos, 4);
            int s32 = BinaryPrimitives.ReadInt32BigEndian(buf.Slice(pos, 4));
            pos += 4;
            return (long)s32;
        }
        if (first == 0xd3)
        {
            Need(buf, pos, 8);
            long s64 = BinaryPrimitives.ReadInt64BigEndian(buf.Slice(pos, 8));
            pos += 8;
            return s64;
        }

        // ---- fixext 1 / 2 / 4 / 8 / 16 ----
        if (first == 0xd4) return ReadExt(buf, ref pos, 1);
        if (first == 0xd5) return ReadExt(buf, ref pos, 2);
        if (first == 0xd6) return ReadExt(buf, ref pos, 4);
        if (first == 0xd7) return ReadExt(buf, ref pos, 8);
        if (first == 0xd8) return ReadExt(buf, ref pos, 16);

        // ---- str 8 / 16 / 32 ----
        if (first == 0xd9) return ReadStr(buf, ref pos, ReadUInt8(buf, ref pos));
        if (first == 0xda) return ReadStr(buf, ref pos, ReadUInt16BE(buf, ref pos));
        if (first == 0xdb) return ReadStr(buf, ref pos, (int)ReadUInt32BE(buf, ref pos));

        // ---- array 16 / 32 ----
        if (first == 0xdc) return ReadArray(buf, ref pos, ReadUInt16BE(buf, ref pos), depth);
        if (first == 0xdd) return ReadArray(buf, ref pos, (int)ReadUInt32BE(buf, ref pos), depth);

        // ---- map 16 / 32 ----
        if (first == 0xde) return ReadMap(buf, ref pos, ReadUInt16BE(buf, ref pos), depth);
        if (first == 0xdf) return ReadMap(buf, ref pos, (int)ReadUInt32BE(buf, ref pos), depth);

        // ---- negative fixint (0xe0–0xff) ----
        if (first >= 0xe0)
            return (long)(sbyte)first;

        throw new InvalidDataException($"Unknown MessagePack format byte: 0x{first:x2}");
    }

    // ----------------------------------------------------------------
    // Private helpers
    // ----------------------------------------------------------------

    private static bool TryReadOptionalString(
        Dictionary<string, object?> source,
        string name,
        out string? value)
    {
        value = null;
        if (!source.TryGetValue(name, out var raw) || raw is null)
            return true;
        if (raw is not string text)
            return false;
        value = text;
        return true;
    }

    private static bool TryReadOptionalStringArray(
        Dictionary<string, object?> source,
        string name,
        out IReadOnlyList<string>? value)
    {
        value = null;
        if (!source.TryGetValue(name, out var raw) || raw is null)
            return true;
        if (raw is not object?[] items)
            return false;

        var result = new string[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            if (items[index] is not string item)
                return false;
            result[index] = item;
        }
        value = result;
        return true;
    }

    private static bool TryReadOptionalBoolean(
        Dictionary<string, object?> source,
        string name,
        out bool? value)
    {
        value = null;
        if (!source.TryGetValue(name, out var raw) || raw is null)
            return true;
        if (raw is not bool boolean)
            return false;
        value = boolean;
        return true;
    }

    private static bool TryReadOptionalInt32(
        Dictionary<string, object?> source,
        string name,
        out int? value)
    {
        value = null;
        if (!source.TryGetValue(name, out var raw) || raw is null)
            return true;
        if (raw is not long number || number is < int.MinValue or > int.MaxValue)
            return false;
        value = (int)number;
        return true;
    }

    private static bool TryReadOptionalTimestamp(
        Dictionary<string, object?> source,
        string name,
        out DateTimeOffset? value)
    {
        value = null;
        if (!source.TryGetValue(name, out var raw) || raw is null)
            return true;
        if (raw is string text)
        {
            if (!DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed))
                return false;
            value = parsed;
            return true;
        }
        if (raw is not byte[] extension || !TryReadTimestampExtension(extension, out var timestamp))
            return false;
        value = timestamp;
        return true;
    }

    private static bool TryReadTimestampExtension(
        ReadOnlySpan<byte> extension,
        out DateTimeOffset value)
    {
        value = default;
        if (extension.Length is not (5 or 9 or 13) || extension[0] != byte.MaxValue)
            return false;

        try
        {
            uint nanoseconds;
            long seconds;
            if (extension.Length == 5)
            {
                nanoseconds = 0;
                seconds = BinaryPrimitives.ReadUInt32BigEndian(extension[1..]);
            }
            else if (extension.Length == 9)
            {
                var packed = BinaryPrimitives.ReadUInt64BigEndian(extension[1..]);
                nanoseconds = (uint)(packed >> 34);
                seconds = (long)(packed & 0x00000003FFFFFFFFUL);
            }
            else
            {
                nanoseconds = BinaryPrimitives.ReadUInt32BigEndian(extension.Slice(1, 4));
                seconds = BinaryPrimitives.ReadInt64BigEndian(extension[5..]);
            }

            if (nanoseconds >= 1_000_000_000)
                return false;
            value = DateTimeOffset.FromUnixTimeSeconds(seconds)
                .AddTicks(nanoseconds / 100);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static Dictionary<string, object?> ReadMap(ReadOnlySpan<byte> buf, ref int pos, int count, int depth)
    {
        EnsureCount(buf, pos, count);
        var dict = new Dictionary<string, object?>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            var key = ReadValue(buf, ref pos, depth + 1) as string
                ?? throw new InvalidDataException("MessagePack map key must be a string");
            var val = ReadValue(buf, ref pos, depth + 1);
            dict[key] = val;
        }
        return dict;
    }

    private static object?[] ReadArray(ReadOnlySpan<byte> buf, ref int pos, int count, int depth)
    {
        EnsureCount(buf, pos, count);
        var arr = new object?[count];
        for (int i = 0; i < count; i++)
            arr[i] = ReadValue(buf, ref pos, depth + 1);
        return arr;
    }

    // 容器声明的元素数不得超过剩余字节数(每元素至少占 1 字节)。挡住 array32/map32 用巨量/负数
    // count 触发的预分配(new object?[count])内存放大 DoS,并把越界 count 归为可捕获的 InvalidDataException。
    private static void EnsureCount(ReadOnlySpan<byte> buf, int pos, int count)
    {
        if (count < 0 || count > buf.Length - pos)
            throw new InvalidDataException($"MessagePack: implausible container count {count}");
    }

    private static string ReadStr(ReadOnlySpan<byte> buf, ref int pos, int byteLen)
    {
        Need(buf, pos, byteLen);
        var s = Encoding.UTF8.GetString(buf.Slice(pos, byteLen));
        pos += byteLen;
        return s;
    }

    private static byte[] ReadBin(ReadOnlySpan<byte> buf, ref int pos, int byteLen)
    {
        Need(buf, pos, byteLen);
        var data = buf.Slice(pos, byteLen).ToArray();
        pos += byteLen;
        return data;
    }

    /// <summary>
    /// Reads an ext value: 1 type byte + <paramref name="dataLen"/> data bytes.
    /// Returns them concatenated as byte[] (type byte first).
    /// </summary>
    private static byte[] ReadExt(ReadOnlySpan<byte> buf, ref int pos, int dataLen)
    {
        Need(buf, pos, 1 + dataLen);
        var result = new byte[1 + dataLen];
        result[0] = buf[pos]; // type byte
        buf.Slice(pos + 1, dataLen).CopyTo(result.AsSpan(1));
        pos += 1 + dataLen;
        return result;
    }

    private static int ReadUInt8(ReadOnlySpan<byte> buf, ref int pos)
    {
        Need(buf, pos, 1);
        return buf[pos++];
    }

    private static int ReadUInt16BE(ReadOnlySpan<byte> buf, ref int pos)
    {
        Need(buf, pos, 2);
        int v = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(pos, 2));
        pos += 2;
        return v;
    }

    private static uint ReadUInt32BE(ReadOnlySpan<byte> buf, ref int pos)
    {
        Need(buf, pos, 4);
        uint v = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(pos, 4));
        pos += 4;
        return v;
    }

    private static void Need(ReadOnlySpan<byte> buf, int pos, int count)
    {
        if (pos + count > buf.Length)
            throw new InvalidDataException(
                $"MessagePack: need {count} bytes at pos {pos} but only {buf.Length - pos} remain");
    }
}
