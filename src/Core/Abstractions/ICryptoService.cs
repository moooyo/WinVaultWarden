using Core.Enums;

namespace Core.Abstractions;

// 纯函数式加密服务。所有方法无副作用,不持有可变状态。
// 说明:SymmetricCryptoKey / EncString / KdfConfig 是 Crypto 项目的类型。
// 为遵守“Core 不依赖 Crypto”,涉及这些类型的解密方法(StretchMasterKey /
// DecryptUserKey / Decrypt / Encrypt / DecryptRsa)定义在 Crypto 项目的
// CryptoService 具体类上;Core 的 ICryptoService 只暴露字节进字节出的两个
// KDF 方法,供需要抽象的上层使用。
public interface ICryptoService
{
    // 由主密码 + 邮箱 + KDF 参数派生 32 字节 MasterKey。
    byte[] DeriveMasterKey(string password, string email, KdfType kdfType, int iterations, int? memoryMiB, int? parallelism);

    // 计算发送给服务端的 MasterPasswordHash(base64)。
    string ComputeMasterPasswordHash(byte[] masterKey, string password);
}
