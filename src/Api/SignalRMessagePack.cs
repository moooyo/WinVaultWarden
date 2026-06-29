using System.Buffers.Binary;
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
        try
        {
            int pos = 0;

            // --- 1. Read the 7-bit varint length prefix ---
            if (pos >= frame.Length) return false;
            int msgLen = 0;
            int shift = 0;
            byte b;
            do
            {
                if (pos >= frame.Length) return false;
                b = frame[pos++];
                msgLen |= (b & 0x7f) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);

            // Remaining bytes must be at least msgLen (there may be no trailing data)
            if (pos + msgLen > frame.Length) return false;
            var payload = frame.Slice(pos, msgLen);

            // --- 2. Decode the MessagePack value ---
            int ppos = 0;
            var value = ReadValue(payload, ref ppos);

            // --- 3. Validate outer array: length >= 5, [0]==1L, [3]=="ReceiveMessage" ---
            if (value is not object?[] arr) return false;
            if (arr.Length < 5) return false;
            if (arr[0] is not long msgType || msgType != 1L) return false;
            if (arr[3] is not string methodName || methodName != "ReceiveMessage") return false;

            // --- 4. Unpack args ---
            if (arr[4] is not object?[] args || args.Length < 1) return false;
            if (args[0] is not Dictionary<string, object?> argsDict) return false;

            int notifType = 0;
            if (argsDict.TryGetValue("Type", out var typeVal) && typeVal is long tl)
                notifType = (int)tl;

            string? entityId = null;
            if (argsDict.TryGetValue("Payload", out var payloadVal) &&
                payloadVal is Dictionary<string, object?> payloadDict)
            {
                if (payloadDict.TryGetValue("Id", out var idVal) && idVal is string idStr)
                    entityId = idStr;
            }

            msg = new NotificationMessage(notifType, entityId);
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
    public static object? ReadValue(ReadOnlySpan<byte> buf, ref int pos)
    {
        if (pos >= buf.Length)
            throw new InvalidDataException("Unexpected end of MessagePack data");

        byte first = buf[pos++];

        // ---- positive fixint (0x00–0x7f) ----
        if (first <= 0x7f)
            return (long)first;

        // ---- fixmap (0x80–0x8f) ----
        if (first >= 0x80 && first <= 0x8f)
            return ReadMap(buf, ref pos, first & 0x0f);

        // ---- fixarray (0x90–0x9f) ----
        if (first >= 0x90 && first <= 0x9f)
            return ReadArray(buf, ref pos, first & 0x0f);

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
        if (first == 0xdc) return ReadArray(buf, ref pos, ReadUInt16BE(buf, ref pos));
        if (first == 0xdd) return ReadArray(buf, ref pos, (int)ReadUInt32BE(buf, ref pos));

        // ---- map 16 / 32 ----
        if (first == 0xde) return ReadMap(buf, ref pos, ReadUInt16BE(buf, ref pos));
        if (first == 0xdf) return ReadMap(buf, ref pos, (int)ReadUInt32BE(buf, ref pos));

        // ---- negative fixint (0xe0–0xff) ----
        if (first >= 0xe0)
            return (long)(sbyte)first;

        throw new InvalidDataException($"Unknown MessagePack format byte: 0x{first:x2}");
    }

    // ----------------------------------------------------------------
    // Private helpers
    // ----------------------------------------------------------------

    private static Dictionary<string, object?> ReadMap(ReadOnlySpan<byte> buf, ref int pos, int count)
    {
        var dict = new Dictionary<string, object?>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            var key = ReadValue(buf, ref pos) as string
                ?? throw new InvalidDataException("MessagePack map key must be a string");
            var val = ReadValue(buf, ref pos);
            dict[key] = val;
        }
        return dict;
    }

    private static object?[] ReadArray(ReadOnlySpan<byte> buf, ref int pos, int count)
    {
        var arr = new object?[count];
        for (int i = 0; i < count; i++)
            arr[i] = ReadValue(buf, ref pos);
        return arr;
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
