namespace Core.Models;

public static class KdfParameterPolicy
{
    public const int MinimumPbkdf2ReadIterations = 100_000;
    public const int MaximumPbkdf2ReadIterations = 10_000_000;
    public const int MinimumPbkdf2WriteIterations = 600_000;
    public const int MaximumPbkdf2WriteIterations = 2_000_000;

    public const int MinimumArgon2ReadIterations = 1;
    public const int MaximumArgon2ReadIterations = 10;
    public const int MinimumArgon2MemoryMiB = 15;
    public const int MaximumArgon2MemoryMiB = 1024;
    public const int MinimumArgon2Parallelism = 1;
    public const int MaximumArgon2Parallelism = 16;

    public static bool IsPbkdf2WriteCompatible(int iterations) =>
        iterations is >= MinimumPbkdf2WriteIterations
            and <= MaximumPbkdf2WriteIterations;
}
