namespace App.Services;

/// <summary>Limits cleanup to old metadata files created by the enrollment service.</summary>
public static class PreparedAppInstallerRetention
{
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);

    public static bool CanRemove(string fileName, DateTimeOffset lastWrite, DateTimeOffset now)
    {
        const string prefix = "WinVaultWarden-";
        const string suffix = ".appinstaller";
        return fileName.Length == prefix.Length + 32 + suffix.Length
            && fileName.StartsWith(prefix, StringComparison.Ordinal)
            && fileName.EndsWith(suffix, StringComparison.Ordinal)
            && Guid.TryParseExact(fileName.Substring(prefix.Length, 32), "N", out _)
            && lastWrite <= now
            && now - lastWrite >= RetentionPeriod;
    }
}
