namespace App.Services;

/// <summary>
/// Coordinates the user's opt-in daily check. It never installs or executes a release asset.
/// </summary>
public static class AutomaticReleaseCheck
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ReleaseUpdateResult?> CheckIfDueAsync(
        string currentVersion,
        string architecture,
        bool isPackaged,
        CancellationToken cancellationToken = default)
    {
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return null;
        try
        {
            var preferences = AppPreferences.Current;
            var now = DateTimeOffset.UtcNow;
            if (!preferences.AutomaticallyCheckForUpdates
                || !ReleaseUpdateService.ShouldAutomaticallyCheck(now, preferences.LastUpdateCheckUtc))
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            preferences.LastUpdateCheckUtc = now;
            AppPreferences.Save();
            using var service = new ReleaseUpdateService();
            return await service.CheckAsync(
                currentVersion, architecture, isPackaged, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }
}
