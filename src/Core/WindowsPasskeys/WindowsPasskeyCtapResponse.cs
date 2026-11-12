using System.Formats.Cbor;

namespace Core.WindowsPasskeys;

/// <summary>
/// CTAP2 response maps consumed by the Windows plugin API. These are authenticator
/// responses with integer map keys, not WebAuthn browser attestation JSON objects.
/// </summary>
public static class WindowsPasskeyCtapResponse
{
    public static byte[] MakeCredential(ReadOnlySpan<byte> authenticatorData)
    {
        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(3);
        writer.WriteInt32(1);
        writer.WriteTextString("none");
        writer.WriteInt32(2);
        writer.WriteByteString(authenticatorData);
        writer.WriteInt32(3);
        writer.WriteStartMap(0);
        writer.WriteEndMap();
        writer.WriteEndMap();
        return writer.Encode();
    }

    public static byte[] GetAssertion(
        ReadOnlySpan<byte> credentialId,
        ReadOnlySpan<byte> authenticatorData,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> userId)
    {
        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(4);
        writer.WriteInt32(1);
        writer.WriteStartMap(2);
        writer.WriteTextString("id");
        writer.WriteByteString(credentialId);
        writer.WriteTextString("type");
        writer.WriteTextString("public-key");
        writer.WriteEndMap();
        writer.WriteInt32(2);
        writer.WriteByteString(authenticatorData);
        writer.WriteInt32(3);
        writer.WriteByteString(signature);
        writer.WriteInt32(4);
        writer.WriteStartMap(1);
        writer.WriteTextString("id");
        writer.WriteByteString(userId);
        writer.WriteEndMap();
        writer.WriteEndMap();
        return writer.Encode();
    }

    public static byte[] AuthenticatorInfo()
    {
        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(5);
        writer.WriteInt32(1);
        writer.WriteStartArray(2);
        writer.WriteTextString("FIDO_2_0");
        writer.WriteTextString("FIDO_2_1");
        writer.WriteEndArray();
        writer.WriteInt32(3);
        writer.WriteByteString(Convert.FromHexString(WindowsPasskeyIdentity.Aaguid.ToString("N")));
        writer.WriteInt32(4);
        writer.WriteStartMap(3);
        writer.WriteTextString("rk"); writer.WriteBoolean(true);
        writer.WriteTextString("up"); writer.WriteBoolean(true);
        writer.WriteTextString("uv"); writer.WriteBoolean(true);
        writer.WriteEndMap();
        writer.WriteInt32(9);
        writer.WriteStartArray(1);
        writer.WriteTextString("internal");
        writer.WriteEndArray();
        writer.WriteInt32(10);
        writer.WriteStartArray(1);
        writer.WriteStartMap(2);
        writer.WriteTextString("alg"); writer.WriteInt32(-7);
        writer.WriteTextString("type"); writer.WriteTextString("public-key");
        writer.WriteEndMap();
        writer.WriteEndArray();
        writer.WriteEndMap();
        return writer.Encode();
    }
}
