namespace Core.Services;

public enum ImportProtection
{
    None,
    Password,
    Account,
}

/// <summary>Bounds allocation and parsing work for explicitly selected vault backups.</summary>
public static class VaultPortingLimits
{
    public const ulong MaximumFileSizeBytes = 64UL * 1024 * 1024;
    public const int MaximumContentCharacters = 32 * 1024 * 1024;
    public const int MinimumExportPasswordLength = 12;

    public static void ValidateContent(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > MaximumContentCharacters)
            throw new InvalidDataException("The vault backup exceeds the supported size limit.");
    }
}
