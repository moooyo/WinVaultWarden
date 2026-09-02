using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Models;
using Crypto;

namespace Vault;

internal sealed class CipherBlobCodec
{
    private const int FormatVersion = 1;
    private const int MaximumOpaqueCharacters = 8 * 1024 * 1024;
    private readonly CryptoService _crypto;

    internal CipherBlobCodec(CryptoService crypto) => _crypto = crypto;

    internal bool IsBlob(string? opaque)
    {
        if (string.IsNullOrWhiteSpace(opaque))
            return false;
        if (opaque.Length > MaximumOpaqueCharacters)
            throw new InvalidDataException("Cipher.data exceeds the size limit.");

        try
        {
            using var document = JsonDocument.Parse(opaque);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            return document.RootElement.EnumerateObject()
                .Any(property => property.NameEquals("format_version"));
        }
        catch (JsonException exception)
        {
            if (opaque.Contains("\"format_version\"", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Cipher.data declares a blob format but is not valid JSON.",
                    exception);
            }
            return false;
        }
    }

    internal CipherBlobContent Decrypt(
        string opaque,
        SymmetricCryptoKey itemKey)
    {
        var container = ParseContainer(opaque);
        var plaintext = CoseDataEnvelopeCodec.Unseal(
            container.WrappedCek,
            container.Envelope,
            itemKey,
            _crypto);
        try
        {
            return CipherBlobV1CborCodec.Decode(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal string Encrypt(Cipher cipher, SymmetricCryptoKey itemKey)
    {
        var plaintext = CipherBlobV1CborCodec.Encode(cipher);
        try
        {
            var sealedData = CoseDataEnvelopeCodec.Seal(plaintext, itemKey, _crypto);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("format_version", FormatVersion);
                writer.WriteString("wrapped_cek", sealedData.WrappedCek);
                writer.WriteString("envelope", sealedData.Envelope);
                writer.WriteEndObject();
                writer.Flush();
            }

            var json = stream.ToArray();
            try
            {
                return Encoding.UTF8.GetString(json);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static CipherBlobContainer ParseContainer(string opaque)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opaque);
        if (opaque.Length > MaximumOpaqueCharacters)
            throw new InvalidDataException("Cipher.data exceeds the size limit.");

        try
        {
            using var document = JsonDocument.Parse(
                opaque,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Cipher.data blob must be a JSON object.");

            int? version = null;
            string? wrappedCek = null;
            string? envelope = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw new InvalidDataException("Cipher.data blob contains a duplicate property.");
                switch (property.Name)
                {
                    case "format_version" when property.Value.ValueKind == JsonValueKind.Number:
                        version = property.Value.GetInt32();
                        break;
                    case "wrapped_cek" when property.Value.ValueKind == JsonValueKind.String:
                        wrappedCek = property.Value.GetString();
                        break;
                    case "envelope" when property.Value.ValueKind == JsonValueKind.String:
                        envelope = property.Value.GetString();
                        break;
                    default:
                        throw new InvalidDataException(
                            "Cipher.data blob contains an unknown or mistyped property.");
                }
            }
            if (version != FormatVersion
                || string.IsNullOrWhiteSpace(wrappedCek)
                || string.IsNullOrWhiteSpace(envelope))
            {
                throw new InvalidDataException(
                    "Cipher.data blob has an unsupported version or missing field.");
            }
            return new CipherBlobContainer(wrappedCek, envelope);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Cipher.data blob is not valid JSON.", exception);
        }
    }

    private sealed record CipherBlobContainer(string WrappedCek, string Envelope);
}
