namespace Core.Session;

/// <summary>
/// Couples a successful authenticated write response with the exact access-token binding
/// attached to the request that produced that response.
/// </summary>
public sealed record AuthenticatedWriteReceipt<T>(
    T Value,
    AccessTokenBinding Authorization);
