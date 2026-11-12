using Core.Enums;

namespace Core.Models;

/// <summary>Explicit parameters for a master-password KDF transition.</summary>
public sealed record AccountKdfParameters(
    KdfType Type,
    int Iterations,
    int? MemoryMiB = null,
    int? Parallelism = null)
{
    public static AccountKdfParameters Pbkdf2(int iterations = 600_000) =>
        new(KdfType.Pbkdf2, iterations);

    public static AccountKdfParameters Argon2id(
        int iterations = 3,
        int memoryMiB = 64,
        int parallelism = 4) => new(KdfType.Argon2id, iterations, memoryMiB, parallelism);

    public bool IsWriteCompatible => Type switch
    {
        KdfType.Pbkdf2 => KdfParameterPolicy.IsPbkdf2WriteCompatible(Iterations)
            && MemoryMiB is null
            && Parallelism is null,
        KdfType.Argon2id => Iterations is >= KdfParameterPolicy.MinimumArgon2ReadIterations
                and <= KdfParameterPolicy.MaximumArgon2ReadIterations
            && MemoryMiB is >= KdfParameterPolicy.MinimumArgon2MemoryMiB
                and <= KdfParameterPolicy.MaximumArgon2MemoryMiB
            && Parallelism is >= KdfParameterPolicy.MinimumArgon2Parallelism
                and <= KdfParameterPolicy.MaximumArgon2Parallelism,
        _ => false,
    };
}
