using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace App.Services;

public sealed partial class StartupRegistrationService
{
    [SupportedOSPlatform("windows10.0.17763.0")]
    public StartupRegistrationService()
        : this(new WindowsStartupRegistrationBackend())
    {
    }
}

/// <summary>
/// Uses StartupTask for MSIX and a single per-user Run entry for portable installations.
/// Windows startup policy remains authoritative; no StartupApproved data is written.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed partial class WindowsStartupRegistrationBackend : IStartupRegistrationBackend
{
    private const int NoPackageIdentity = 15700;
    private const int InsufficientBuffer = 122;

    public async Task<StartupRegistrationResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var packaged = IsPackaged();
            if (packaged is null)
                return Unavailable();
            if (packaged.Value)
            {
                var task = await StartupTask.GetAsync(StartupRegistrationService.StartupTaskId)
                    .AsTask(cancellationToken);
                return FromPackagedState(task.State);
            }

            return ReadPortable();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Failed();
        }
    }

    public async Task<StartupRegistrationResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var packaged = IsPackaged();
            if (packaged is null)
                return Unavailable();
            if (packaged.Value)
            {
                var task = await StartupTask.GetAsync(StartupRegistrationService.StartupTaskId)
                    .AsTask(cancellationToken);
                if (!enabled)
                {
                    if (task.State == StartupTaskState.Enabled)
                        task.Disable();
                    return FromPackagedState(task.State);
                }

                if (task.State == StartupTaskState.Disabled)
                {
                    // This must be called from an explicit UI action. Windows owns consent and policy.
                    return FromPackagedState(await task.RequestEnableAsync().AsTask(cancellationToken));
                }
                return FromPackagedState(task.State);
            }

            return SetPortableEnabled(enabled, cancellationToken);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Failed();
        }
    }

    private static StartupRegistrationResult ReadPortable()
    {
        if (!TryGetPortableCommand(out var command))
            return Unavailable();
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistrationService.PortableRunKey);
        var stored = key?.GetValue(
            StartupRegistrationService.PortableValueName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        return PortableState(stored, command);
    }

    private static StartupRegistrationResult SetPortableEnabled(
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!TryGetPortableCommand(out var command))
            return Unavailable();

        using var key = Registry.CurrentUser.CreateSubKey(
            StartupRegistrationService.PortableRunKey,
            writable: true);
        var stored = key.GetValue(
            StartupRegistrationService.PortableValueName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (stored is not null
            && (stored is not string text
                || !string.Equals(text, command, StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (enabled)
            key.SetValue(StartupRegistrationService.PortableValueName, command, RegistryValueKind.String);
        else
            key.DeleteValue(StartupRegistrationService.PortableValueName, throwOnMissingValue: false);

        key.Flush();
        return PortableState(key.GetValue(StartupRegistrationService.PortableValueName), command);
    }

    private static bool TryGetPortableCommand(out string command)
    {
        var path = Environment.ProcessPath;
        if (path is null || !File.Exists(path))
        {
            command = string.Empty;
            return false;
        }
        return StartupRegistrationService.TryCreatePortableCommand(path, out command);
    }

    private static StartupRegistrationResult PortableState(object? stored, string command) => stored switch
    {
        null => new(StartupRegistrationState.Disabled, "Start at sign-in is disabled."),
        string text when string.Equals(text, command, StringComparison.OrdinalIgnoreCase) =>
            new(StartupRegistrationState.Registered,
                "A startup command is registered for this user. Windows Startup settings can still disable it."),
        _ => Conflict(),
    };

    private static StartupRegistrationResult FromPackagedState(StartupTaskState state) => state switch
    {
        StartupTaskState.Disabled => new(
            StartupRegistrationState.Disabled, "Start at sign-in is disabled."),
        StartupTaskState.Enabled => new(
            StartupRegistrationState.Enabled, "Start at sign-in is enabled."),
        StartupTaskState.EnabledByPolicy => new(
            StartupRegistrationState.EnabledByPolicy, "Start at sign-in is enabled by Windows policy."),
        StartupTaskState.DisabledByUser => new(
            StartupRegistrationState.DisabledByUser,
            "Windows disabled this startup task. Enable it in Windows Settings > Apps > Startup."),
        StartupTaskState.DisabledByPolicy => new(
            StartupRegistrationState.DisabledByPolicy,
            "Windows policy prevents this app from starting at sign-in."),
        _ => Unavailable(),
    };

    private static StartupRegistrationResult Conflict() => new(
        StartupRegistrationState.ConflictingRegistration,
        "Another installation owns the WinVaultWarden startup entry. Disable that installation's startup entry first.");

    private static StartupRegistrationResult Unavailable() => new(
        StartupRegistrationState.Unavailable,
        "Startup registration is unavailable for this installation or executable path.");

    private static StartupRegistrationResult Failed() => new(
        StartupRegistrationState.Failed,
        "Windows could not read or change the startup registration. No successful change is claimed.");

    private static bool IsRecoverable(Exception exception) => exception is
        IOException or UnauthorizedAccessException or SecurityException or COMException
        or ArgumentException or InvalidOperationException or PlatformNotSupportedException;

    private static bool? IsPackaged()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) switch
        {
            NoPackageIdentity => false,
            0 or InsufficientBuffer => true,
            _ => null,
        };
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFullName")]
    private static partial int GetCurrentPackageFullName(ref uint length, IntPtr packageName);
}
