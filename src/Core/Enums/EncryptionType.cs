namespace Core.Enums;

// Bitwarden EncString encryption types.
public enum EncryptionType
{
    AesCbc256_B64 = 0,
    AesCbc128_HmacSha256_B64 = 1,
    AesCbc256_HmacSha256_B64 = 2,
    Rsa2048_OaepSha256_B64 = 3,
    Rsa2048_OaepSha1_B64 = 4,
    Rsa2048_OaepSha256_HmacSha256_B64 = 5,
    Rsa2048_OaepSha1_HmacSha256_B64 = 6,
}

public static class EncryptionTypeExtensions
{
    public static bool IsRsaEnvelope(this EncryptionType type) => type is
        EncryptionType.Rsa2048_OaepSha256_B64
            or EncryptionType.Rsa2048_OaepSha1_B64
            or EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64
            or EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64;

    public static bool HasLegacyRsaMac(this EncryptionType type) => type is
        EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64
            or EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64;
}
