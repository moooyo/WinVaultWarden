using Core.Enums;

namespace Crypto;

// KDF 参数,来自 prelogin 响应。
public sealed record KdfConfig(KdfType KdfType, int Iterations, int? MemoryMiB, int? Parallelism)
{
    public static KdfConfig Pbkdf2Default => new(KdfType.Pbkdf2, 600_000, null, null);
    public static KdfConfig Argon2idDefault => new(KdfType.Argon2id, 3, 64, 4);
}
