namespace App.Services;

public enum StartupRegistrationState
{
    Disabled,
    Enabled,
    EnabledByPolicy,
    Registered,
    DisabledByUser,
    DisabledByPolicy,
    ConflictingRegistration,
    Unavailable,
    Failed,
}

public sealed record StartupRegistrationResult(
    StartupRegistrationState State,
    string Message)
{
    public bool IsRequestedEnabled =>
        State is StartupRegistrationState.Enabled or StartupRegistrationState.EnabledByPolicy
            or StartupRegistrationState.Registered;

    public bool CanChange => State is not (
        StartupRegistrationState.EnabledByPolicy or StartupRegistrationState.DisabledByPolicy
            or StartupRegistrationState.Unavailable);
}

public interface IStartupRegistrationBackend
{
    Task<StartupRegistrationResult> ReadAsync(CancellationToken cancellationToken);
    Task<StartupRegistrationResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken);
}

/// <summary>
/// Serializes explicit startup changes and returns the operating system's actual result.
/// It does not silently re-enable a startup task that the user disabled in Windows.
/// </summary>
public sealed partial class StartupRegistrationService
{
    public const string StartupTaskId = "WinVaultWardenStartup";
    public const string PortableValueName = "WinVaultWarden";
    public const string PortableRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly IStartupRegistrationBackend _backend;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StartupRegistrationService(IStartupRegistrationBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async Task<StartupRegistrationResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await _backend.ReadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StartupRegistrationResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await _backend.SetEnabledAsync(enabled, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Builds a single executable command for the documented per-user Run-key limit.
    /// A local absolute executable path is required; shell commands and UNC paths are rejected.
    /// </summary>
    public static bool TryCreatePortableCommand(string? executablePath, out string command)
    {
        command = string.Empty;
        if (string.IsNullOrWhiteSpace(executablePath)
            || executablePath.Length < 7
            || !char.IsAsciiLetter(executablePath[0])
            || executablePath[1] != ':'
            || executablePath[2] is not ('\\' or '/')
            || executablePath.Contains('"')
            || executablePath.Any(char.IsControl)
            || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var proposed = $"\"{executablePath}\" --startup";
        if (proposed.Length > 260)
            return false;

        command = proposed;
        return true;
    }
}
