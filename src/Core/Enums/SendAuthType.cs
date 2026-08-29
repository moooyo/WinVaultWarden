namespace Core.Enums;

/// <summary>
/// Authentication mode reported for a Send. The numeric values follow the Bitwarden wire contract.
/// </summary>
public enum SendAuthType
{
    Unknown = -1,
    Email = 0,
    Password = 1,
    None = 2,
}
