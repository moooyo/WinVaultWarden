using Windows.System;

namespace App.Services;

public sealed class WindowsExternalUriLauncher : IExternalUriLauncher
{
    public async Task<bool> LaunchAsync(Uri uri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ct.ThrowIfCancellationRequested();
        var launched = await Launcher.LaunchUriAsync(uri);
        ct.ThrowIfCancellationRequested();
        return launched;
    }
}
