using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace Core.SshAgent;

/// <summary>A bounded, read-only subset of the RFC 9987 agent protocol.</summary>
public static class SshAgentProtocol
{
    public const int MaximumPacketLength = 1_048_576;
    public const int MaximumResponseLength = 262_144;
    public const int MaximumPublicKeyLength = 16_384;
    public const int MaximumIdentityCount = 256;
    public const byte Failure = 5;
    public const byte RequestIdentities = 11;
    public const byte IdentitiesAnswer = 12;
    public const byte SignRequest = 13;
    public const byte SignResponse = 14;
    public const byte Extension = 27;
    public const byte ExtensionFailure = 28;

    public static async Task ServeAsync(
        Stream stream,
        ISshAgentRequestHandler handler,
        ISshAgentClientIdentity client,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(client);
        while (!ct.IsCancellationRequested && client.IsCurrent)
        {
            var packet = await ReadPacketAsync(stream, ct).ConfigureAwait(false);
            if (packet is null)
                return;
            byte[]? response = null;
            try
            {
                response = await HandlePacketAsync(packet, handler, client, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!client.IsCurrent)
                    return;
                await WritePacketAsync(stream, response, ct).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(packet);
                if (response is not null)
                    CryptographicOperations.ZeroMemory(response);
            }
        }
    }

    public static async Task<byte[]> HandlePacketAsync(
        byte[] packet,
        ISshAgentRequestHandler handler,
        ISshAgentClientIdentity client,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(client);
        ct.ThrowIfCancellationRequested();
        if (packet.Length is 0 or > MaximumPacketLength || !client.IsCurrent)
            return [Failure];

        byte[]? key = null;
        byte[]? data = null;
        try
        {
            if (packet[0] == RequestIdentities)
            {
                if (packet.Length != 1)
                    return [Failure];
                var identities = await handler.GetIdentitiesAsync(client, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!client.IsCurrent || identities.Count > MaximumIdentityCount)
                    return [Failure];
                using var result = new MemoryStream();
                result.WriteByte(IdentitiesAnswer);
                WriteUInt32(result, 0);
                uint count = 0;
                foreach (var identity in identities)
                {
                    if (identity.PublicKeyBlob.Length is 0 or > MaximumPublicKeyLength)
                        return [Failure];
                    // Vault entry names are untrusted display metadata. Do not send terminal
                    // control or bidi-format characters to clients such as ssh-add -L.
                    var safeComment = new string(identity.Comment.Take(256)
                        .Select(character => char.GetUnicodeCategory(character)
                            is UnicodeCategory.Control or UnicodeCategory.Format ? '\uFFFD' : character)
                        .ToArray());
                    var comment = Encoding.UTF8.GetBytes(safeComment);
                    // OpenSSH clients impose a 256 KiB reply limit independently of
                    // the request limit. Count only complete identities that fit it.
                    if (result.Length + 8 + identity.PublicKeyBlob.Length + comment.Length > MaximumResponseLength)
                        break;
                    WriteString(result, identity.PublicKeyBlob);
                    WriteString(result, comment);
                    count++;
                }
                result.Position = 1;
                WriteUInt32(result, count);
                return result.ToArray();
            }

            // Extension support is intentionally absent. In particular, do not claim a
            // destination identity or session binding that has not been cryptographically verified.
            if (packet[0] == Extension)
                return [Failure];
            if (packet[0] != SignRequest)
                return [Failure];

            var parsed = ParseSignRequest(packet);
            key = parsed.PublicKey;
            data = parsed.Data;
            var signature = await handler.SignAsync(client, key, data, parsed.Flags, ct).ConfigureAwait(false);
            if (signature is null)
                return [Failure];
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!client.IsCurrent || signature.Length > MaximumPublicKeyLength)
                    return [Failure];
                using var result = new MemoryStream();
                result.WriteByte(SignResponse);
                WriteString(result, signature);
                return result.ToArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signature);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException
            or ArgumentException or CryptographicException or NotSupportedException)
        {
            return [Failure];
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
            if (data is not null)
                CryptographicOperations.ZeroMemory(data);
        }
    }

    public static async Task<byte[]?> ReadPacketAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), ct).ConfigureAwait(false);
        if (first == 0)
            return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > MaximumPacketLength)
            throw new InvalidDataException("Invalid SSH agent packet length.");
        var packet = new byte[(int)length];
        try
        {
            await stream.ReadExactlyAsync(packet, ct).ConfigureAwait(false);
            return packet;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(packet);
            throw;
        }
    }

    public static async Task WritePacketAsync(Stream stream, byte[] packet, CancellationToken ct = default)
    {
        if (packet.Length is 0 or > MaximumPacketLength)
            throw new InvalidDataException("Invalid SSH agent response length.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)packet.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(packet, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> encoded = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(encoded, value);
        stream.Write(encoded);
    }

    public static void WriteString(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteUInt32(stream, checked((uint)value.Length));
        stream.Write(value);
    }

    private static (byte[] PublicKey, byte[] Data, uint Flags) ParseSignRequest(byte[] packet)
    {
        var reader = new SshAgentReader(packet.AsSpan(1));
        var key = reader.ReadString(MaximumPublicKeyLength).ToArray();
        byte[]? data = null;
        try
        {
            data = reader.ReadString(MaximumPacketLength).ToArray();
            var flags = reader.ReadUInt32();
            reader.RequireEnd();
            if (key.Length == 0)
                throw new InvalidDataException("An SSH key is required.");
            return (key, data, flags);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            if (data is not null)
                CryptographicOperations.ZeroMemory(data);
            throw;
        }
    }
}

/// <summary>Strict SSH length-prefixed reader shared by public-key validation.</summary>
public ref struct SshAgentReader(ReadOnlySpan<byte> input)
{
    private ReadOnlySpan<byte> _remaining = input;

    public uint ReadUInt32()
    {
        if (_remaining.Length < 4)
            throw new InvalidDataException("An SSH uint32 is truncated.");
        var value = BinaryPrimitives.ReadUInt32BigEndian(_remaining);
        _remaining = _remaining[4..];
        return value;
    }

    public ReadOnlySpan<byte> ReadString(int maximumLength)
    {
        var length = ReadUInt32();
        if (length > (uint)maximumLength || length > (uint)_remaining.Length)
            throw new InvalidDataException("An SSH string length is invalid.");
        var value = _remaining[..(int)length];
        _remaining = _remaining[(int)length..];
        return value;
    }

    public void RequireEnd()
    {
        if (!_remaining.IsEmpty)
            throw new InvalidDataException("Unexpected trailing SSH agent data.");
    }
}
