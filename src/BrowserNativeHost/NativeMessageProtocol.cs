using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace BrowserNativeHost;

public static class NativeMessageProtocol
{
    public const int MaxMessageBytes = 1024 * 1024;

    public static async Task<T?> ReadAsync<T>(Stream input, JsonTypeInfo<T> typeInfo, CancellationToken ct = default)
    {
        var lengthBytes = new byte[4];
        var read = await ReadExactOrEndAsync(input, lengthBytes, lengthBytes.Length, ct);
        if (read == 0)
            return default;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (length > MaxMessageBytes)
            throw new InvalidDataException($"Native message is too large: {length} bytes.");

        var payload = new byte[length];
        await ReadExactOrEndAsync(input, payload, payload.Length, ct);
        return JsonSerializer.Deserialize(payload, typeInfo);
    }

    public static async Task WriteAsync<T>(Stream output, T message, JsonTypeInfo<T> typeInfo, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
        if (payload.Length > MaxMessageBytes)
            throw new InvalidDataException($"Native message is too large: {payload.Length} bytes.");

        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)payload.Length);
        await output.WriteAsync(lengthBytes, ct);
        await output.WriteAsync(payload, ct);
        await output.FlushAsync(ct);
    }

    private static async Task<int> ReadExactOrEndAsync(Stream input, byte[] buffer, int length, CancellationToken ct)
    {
        var total = 0;
        while (total < length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total, length - total), ct);
            if (read == 0)
            {
                if (total == 0)
                    return 0;

                throw new EndOfStreamException("Unexpected end of native message stream.");
            }

            total += read;
        }

        return total;
    }
}
