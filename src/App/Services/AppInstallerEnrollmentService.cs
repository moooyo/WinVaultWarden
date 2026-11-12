using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.System;

namespace App.Services;

public enum AppInstallerEnrollmentState
{
    Portable,
    Available,
    Enrolled,
    OtherSource,
    Unavailable,
}

public sealed record AppInstallerEnrollmentStatus(
    AppInstallerEnrollmentState State,
    string Message,
    Uri? Source = null);

public sealed class PreparedAppInstaller : IDisposable
{
    internal PreparedAppInstaller(string path, byte[] digest, AppInstallerPackageIdentity identity)
    {
        Path = path;
        Digest = digest;
        Identity = identity;
    }

    internal string Path { get; }
    internal byte[] Digest { get; }
    internal AppInstallerPackageIdentity Identity { get; }
    internal bool Launched { get; set; }

    public void Dispose()
    {
        // LaunchFileAsync reports activation, not that App Installer has finished reading.
        // Successfully launched metadata remains available until the bounded retention cleanup.
        if (Launched)
            return;
        try { File.Delete(Path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Hands a validated metadata file to Windows App Installer after the application locks.
/// Windows owns consent, package-signature verification, deployment and automatic servicing.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed partial class AppInstallerEnrollmentService
{
    private const int NoPackageIdentity = 15700;
    private const int InsufficientBuffer = 122;

    /// <summary>May be called at startup; only expired metadata from this package is considered.</summary>
    public void CleanupRetainedMetadata()
    {
        if (ReadPackageIdentity(out var identity) == AppInstallerEnrollmentState.Portable || identity is null)
            return;
        try
        {
            var folder = System.IO.Path.Combine(ApplicationData.Current.TemporaryFolder.Path, "WinVaultWardenUpdates");
            if (Directory.Exists(folder))
                RemoveExpiredMetadata(folder);
        }
        catch (Exception exception) when (IsRecoverable(exception)) { }
    }

    public AppInstallerEnrollmentStatus ReadStatus()
    {
        var packageState = ReadPackageIdentity(out var identity);
        if (packageState == AppInstallerEnrollmentState.Portable)
        {
            return new(packageState,
                "Automatic installation is available for MSIX installations. Portable updates require a separately verified bundle and replacement transaction.");
        }
        if (identity is null || packageState == AppInstallerEnrollmentState.Unavailable
            || !AppInstallerFeedPolicy.IsExpectedInstalledIdentity(identity)
            || !AppInstallerFeedPolicy.TryGetFeedUri(identity.Architecture, out var source))
        {
            return new(AppInstallerEnrollmentState.Unavailable,
                "This installation cannot enroll in the official MSIX update channel.");
        }

        try
        {
            var info = Package.Current.GetAppInstallerInfo();
            if (info?.Uri is null)
            {
                return new(AppInstallerEnrollmentState.Available,
                    "Open the official App Installer file once to enroll in Windows-managed automatic updates.", source);
            }
            if (info.Uri != source)
            {
                return new(AppInstallerEnrollmentState.OtherSource,
                    "Windows has associated this package with a different installer source. Review the official channel before changing it.", source);
            }
            if (ApiInformation.IsPropertyPresent("Windows.ApplicationModel.AppInstallerInfo", "AutomaticBackgroundTask")
                && ApiInformation.IsPropertyPresent("Windows.ApplicationModel.AppInstallerInfo", "OnLaunch")
                && !info.AutomaticBackgroundTask && !info.OnLaunch)
            {
                return new(AppInstallerEnrollmentState.Enrolled,
                    "Windows has associated the official feed, but its launch and background update settings are disabled. Reopen App Installer to review them.", source);
            }
            return new(AppInstallerEnrollmentState.Enrolled,
                "Windows has associated this package with the official automatic-update feed. Windows policy and App Installer control update timing.", source);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new(AppInstallerEnrollmentState.Unavailable,
                "Windows could not read this package's App Installer association.");
        }
    }

    public async Task<PreparedAppInstaller> PrepareAsync(CancellationToken cancellationToken)
    {
        if (ReadPackageIdentity(out var identity) == AppInstallerEnrollmentState.Unavailable
            || identity is null || !AppInstallerFeedPolicy.IsExpectedInstalledIdentity(identity)
            || !AppInstallerFeedPolicy.TryGetFeedUri(identity.Architecture, out _))
        {
            throw new InvalidOperationException("The installed package cannot use this update feed.");
        }

        using var downloader = new AppInstallerFeedDownloader();
        var content = await downloader.DownloadAsync(identity.Architecture, cancellationToken).ConfigureAwait(false);
        if (!AppInstallerFeedPolicy.TryValidate(content, identity, out _))
            throw new InvalidDataException("The update feed did not match the installed package and official update policy.");

        cancellationToken.ThrowIfCancellationRequested();
        var folder = System.IO.Path.Combine(ApplicationData.Current.TemporaryFolder.Path, "WinVaultWardenUpdates");
        Directory.CreateDirectory(folder);
        RemoveExpiredMetadata(folder);
        var path = System.IO.Path.Combine(folder, $"WinVaultWarden-{Guid.NewGuid():N}.appinstaller");
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await file.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new(path, SHA256.HashData(content), identity);
        }
        catch
        {
            try { File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Call only after the security lifecycle has locked the vault and cleared sensitive UI.
    /// A successful launch means that App Installer opened; it does not mean installation completed.
    /// </summary>
    public async Task<bool> LaunchPreparedAsync(PreparedAppInstaller prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (ReadPackageIdentity(out var identity) == AppInstallerEnrollmentState.Unavailable
            || identity != prepared.Identity || identity is null)
        {
            return false;
        }

        await using var file = new FileStream(prepared.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > AppInstallerFeedPolicy.MaximumFeedBytes)
            return false;
        var content = new byte[checked((int)file.Length)];
        // Keep the caller's UI context for the Windows file-launch operation.
        await file.ReadExactlyAsync(content);
        if (!CryptographicOperations.FixedTimeEquals(prepared.Digest, SHA256.HashData(content))
            || !AppInstallerFeedPolicy.TryValidate(content, identity, out _))
        {
            return false;
        }

        var installer = await StorageFile.GetFileFromPathAsync(prepared.Path);
        var launched = await Launcher.LaunchFileAsync(installer);
        prepared.Launched = launched;
        return launched;
    }

    private static AppInstallerEnrollmentState ReadPackageIdentity(out AppInstallerPackageIdentity? identity)
    {
        identity = null;
        try
        {
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
            if (result == NoPackageIdentity)
                return AppInstallerEnrollmentState.Portable;
            if (result is not (0 or InsufficientBuffer))
                return AppInstallerEnrollmentState.Unavailable;
            var id = Package.Current.Id;
            var version = id.Version;
            identity = new(id.Name, id.Publisher,
                new Version(version.Major, version.Minor, version.Build, version.Revision),
                id.Architecture.ToString().ToLowerInvariant());
            return AppInstallerEnrollmentState.Available;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return AppInstallerEnrollmentState.Unavailable;
        }
    }

    private static void RemoveExpiredMetadata(string folder)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var path in Directory.EnumerateFiles(folder, "WinVaultWarden-*.appinstaller").Take(256))
            {
                try
                {
                    if ((File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0
                        || !PreparedAppInstallerRetention.CanRemove(
                            System.IO.Path.GetFileName(path), File.GetLastWriteTimeUtc(path), now))
                    {
                        continue;
                    }
                    File.Delete(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsRecoverable(Exception exception) => exception is
        COMException or InvalidOperationException or PlatformNotSupportedException
        or IOException or UnauthorizedAccessException;

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFullName")]
    private static partial int GetCurrentPackageFullName(ref uint length, IntPtr packageName);
}
