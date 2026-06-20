namespace Core.Enums;

// Bitwarden EncString encType。结构见 EncString 解析。
public enum EncryptionType
{
    AesCbc256_B64 = 0,              // iv|ct,无 MAC
    AesCbc128_HmacSha256_B64 = 1,  // iv|ct|mac
    AesCbc256_HmacSha256_B64 = 2,  // iv|ct|mac(当前主流)
    Rsa2048_OaepSha256_B64 = 3,    // RSA 密文
    Rsa2048_OaepSha1_B64 = 4,      // RSA 密文
}
