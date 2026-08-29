using System.Text.Json.Serialization;
using Core.Enums;
using Core.Models;

namespace Api.Dtos;

public sealed record PreloginRequest([property: JsonPropertyName("email")] string Email);

public sealed record PreloginKdfSettingsResponse(
    [property: JsonPropertyName("kdfType")] KdfType? KdfType,
    [property: JsonPropertyName("iterations")] int? Iterations,
    [property: JsonPropertyName("memory")] int? Memory,
    [property: JsonPropertyName("parallelism")] int? Parallelism);

public sealed record PreloginResponse(
    [property: JsonPropertyName("kdf")] KdfType? Kdf,
    [property: JsonPropertyName("kdfIterations")] int? KdfIterations,
    [property: JsonPropertyName("kdfMemory")] int? KdfMemory,
    [property: JsonPropertyName("kdfParallelism")] int? KdfParallelism,
    [property: JsonPropertyName("kdfSettings")] PreloginKdfSettingsResponse? KdfSettings = null,
    [property: JsonPropertyName("salt")] string? Salt = null)
{
    public ResolvedPreloginSettings Resolve(string email)
    {
        ResolvedKdfSettings? nested = null;
        if (KdfSettings is not null)
        {
            nested = ResolveKdf(
                KdfSettings.KdfType,
                KdfSettings.Iterations,
                KdfSettings.Memory,
                KdfSettings.Parallelism)
                ?? throw new InvalidDataException(
                    "Prelogin response contains incomplete nested KDF settings.");
        }
        var legacy = ResolveKdf(Kdf, KdfIterations, KdfMemory, KdfParallelism);

        if (nested is not null && legacy is not null && nested != legacy)
            throw new InvalidDataException("Prelogin response contains conflicting KDF settings.");

        var kdf = nested ?? legacy
            ?? throw new InvalidDataException("Prelogin response does not contain complete KDF settings.");
        string salt;
        if (Salt is null)
        {
            salt = MasterPasswordSaltPolicy.Resolve(email, explicitSalt: null);
        }
        else
        {
            try
            {
                salt = MasterPasswordSaltPolicy.Resolve(email, Salt);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException(
                    "Prelogin response contains an invalid master-password salt.",
                    ex);
            }
        }
        return new ResolvedPreloginSettings(
            kdf.KdfType,
            kdf.Iterations,
            kdf.Memory,
            kdf.Parallelism,
            salt);
    }

    private static ResolvedKdfSettings? ResolveKdf(
        KdfType? type,
        int? iterations,
        int? memory,
        int? parallelism)
    {
        if (type is null || iterations is null)
            return null;
        if (type == KdfType.Argon2id && (memory is null || parallelism is null))
            return null;
        return new ResolvedKdfSettings(type.Value, iterations.Value, memory, parallelism);
    }

    private sealed record ResolvedKdfSettings(
        KdfType KdfType,
        int Iterations,
        int? Memory,
        int? Parallelism);
}

public sealed record ResolvedPreloginSettings(
    KdfType KdfType,
    int Iterations,
    int? Memory,
    int? Parallelism,
    string MasterPasswordSalt);
