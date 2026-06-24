using Core.Enums;

namespace Core.Models;

public sealed record PersistedSession(
    string ServerUrl,
    string Email,
    string DeviceIdentifier,
    string RefreshToken,
    string ProtectedUserKey,
    KdfType KdfType,
    int KdfIterations,
    int? KdfMemory,
    int? KdfParallelism);
