using Core.Services;
using Core.Session;

namespace App.Services;

public enum VaultTimeoutAction { Lock, Logout }

public enum VaultTimeoutTrigger
{
    Never,
    OnAppRestart,
    OnSystemLock,
    AfterIdle,
}

public readonly record struct EffectiveVaultTimeout(
    VaultTimeoutTrigger Trigger,
    int Minutes,
    VaultTimeoutAction Action);

public static class IdleTimeoutPolicy
{
    public const int RestartIndex = 0;
    public const int NeverIndex = 7;
    public const int OnSystemLockIndex = 8;
    public const int PolicySpecificMinutesIndex = 9;

    public static bool IsExpired(DateTimeOffset lastActivity, DateTimeOffset now, int minutes) =>
        minutes > 0 && now - lastActivity >= TimeSpan.FromMinutes(minutes);

    public static bool IsExpired(
        DateTimeOffset lastActivity,
        DateTimeOffset now,
        EffectiveVaultTimeout timeout) => timeout.Trigger switch
    {
        VaultTimeoutTrigger.AfterIdle when timeout.Minutes == 0 => now >= lastActivity,
        VaultTimeoutTrigger.AfterIdle => IsExpired(lastActivity, now, timeout.Minutes),
        _ => false,
    };

    public static int MinutesForIndex(int index) =>
        AppPreferenceIndexPolicy.NormalizeSessionTimeoutIndex(index) switch
    {
        1 => 1, 2 => 5, 3 => 15, 4 => 30, 5 => 60, 6 => 240,
        _ => 0,
    };

    public static VaultTimeoutAction ActionForIndex(int index) =>
        AppPreferenceIndexPolicy.NormalizeTimeoutActionIndex(index) == 1
            ? VaultTimeoutAction.Logout
            : VaultTimeoutAction.Lock;

    public static EffectiveVaultTimeout Resolve(
        IVaultSnapshot snapshot,
        int sessionTimeoutIndex,
        int timeoutActionIndex)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var preference = TimeoutForIndex(sessionTimeoutIndex, ActionForIndex(timeoutActionIndex));
        var constraint = MaximumVaultTimeoutPolicyResolver.Resolve(snapshot);
        if (!constraint.Applies)
            return preference;

        var action = constraint.RequiredAction switch
        {
            MaximumVaultTimeoutAction.Lock => VaultTimeoutAction.Lock,
            MaximumVaultTimeoutAction.Logout => VaultTimeoutAction.Logout,
            _ => preference.Action,
        };
        var constrained = ApplyConstraint(preference, constraint);
        return constrained with { Action = action };
    }

    public static bool IsSessionTimeoutIndexAllowed(
        IVaultSnapshot snapshot,
        int index)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var constraint = MaximumVaultTimeoutPolicyResolver.Resolve(snapshot);
        if (!constraint.Applies || constraint.Mode == MaximumVaultTimeoutMode.Never)
            return index is >= RestartIndex and <= OnSystemLockIndex;

        if (index == PolicySpecificMinutesIndex)
        {
            return constraint.Mode == MaximumVaultTimeoutMode.Immediately
                || constraint.Mode == MaximumVaultTimeoutMode.Minutes;
        }

        var normalized = AppPreferenceIndexPolicy.NormalizeSessionTimeoutIndex(index);
        var option = TimeoutForIndex(normalized, VaultTimeoutAction.Lock);
        return constraint.Mode switch
        {
            MaximumVaultTimeoutMode.Immediately => false,
            MaximumVaultTimeoutMode.Minutes =>
                option.Trigger == VaultTimeoutTrigger.AfterIdle
                    && option.Minutes <= constraint.Minutes,
            MaximumVaultTimeoutMode.OnSystemLock =>
                option.Trigger is VaultTimeoutTrigger.AfterIdle
                    or VaultTimeoutTrigger.OnSystemLock,
            MaximumVaultTimeoutMode.OnAppRestart =>
                option.Trigger is VaultTimeoutTrigger.AfterIdle
                    or VaultTimeoutTrigger.OnSystemLock
                    or VaultTimeoutTrigger.OnAppRestart,
            MaximumVaultTimeoutMode.Never => true,
            _ => false,
        };
    }

    public static int NormalizeSessionTimeoutIndexForPolicy(
        IVaultSnapshot snapshot,
        int preferredIndex)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalized = AppPreferenceIndexPolicy.NormalizeSessionTimeoutIndex(preferredIndex);
        if (IsSessionTimeoutIndexAllowed(snapshot, normalized))
            return normalized;

        var constraint = MaximumVaultTimeoutPolicyResolver.Resolve(snapshot);
        return constraint.Mode switch
        {
            MaximumVaultTimeoutMode.Immediately => PolicySpecificMinutesIndex,
            MaximumVaultTimeoutMode.Minutes =>
                IndexForExactMinutes(constraint.Minutes) ?? PolicySpecificMinutesIndex,
            MaximumVaultTimeoutMode.OnSystemLock => OnSystemLockIndex,
            MaximumVaultTimeoutMode.OnAppRestart => RestartIndex,
            _ => normalized,
        };
    }

    public static int NormalizeTimeoutActionIndexForPolicy(
        IVaultSnapshot snapshot,
        int preferredIndex)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var constraint = MaximumVaultTimeoutPolicyResolver.Resolve(snapshot);
        return constraint.RequiredAction switch
        {
            MaximumVaultTimeoutAction.Lock => 0,
            MaximumVaultTimeoutAction.Logout => 1,
            _ => AppPreferenceIndexPolicy.NormalizeTimeoutActionIndex(preferredIndex),
        };
    }

    public static bool HasPolicySpecificMinutes(IVaultSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var constraint = MaximumVaultTimeoutPolicyResolver.Resolve(snapshot);
        return constraint.Applies
            && (constraint.Mode == MaximumVaultTimeoutMode.Immediately
                || constraint.Mode == MaximumVaultTimeoutMode.Minutes
                    && IndexForExactMinutes(constraint.Minutes) is null);
    }

    private static EffectiveVaultTimeout TimeoutForIndex(
        int index,
        VaultTimeoutAction action)
    {
        var normalized = AppPreferenceIndexPolicy.NormalizeSessionTimeoutIndex(index);
        return normalized switch
        {
            RestartIndex => new(VaultTimeoutTrigger.OnAppRestart, 0, action),
            1 or 2 or 3 or 4 or 5 or 6 =>
                new(VaultTimeoutTrigger.AfterIdle, MinutesForIndex(normalized), action),
            NeverIndex => new(VaultTimeoutTrigger.Never, 0, action),
            OnSystemLockIndex => new(VaultTimeoutTrigger.OnSystemLock, 0, action),
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
    }

    private static EffectiveVaultTimeout ApplyConstraint(
        EffectiveVaultTimeout preference,
        MaximumVaultTimeoutConstraint constraint) => constraint.Mode switch
    {
        MaximumVaultTimeoutMode.Never => preference,
        MaximumVaultTimeoutMode.Immediately => preference with
        {
            Trigger = VaultTimeoutTrigger.AfterIdle,
            Minutes = 0,
        },
        MaximumVaultTimeoutMode.Minutes =>
            preference.Trigger == VaultTimeoutTrigger.AfterIdle
                ? preference with { Minutes = Math.Min(preference.Minutes, constraint.Minutes) }
                : preference with
                {
                    Trigger = VaultTimeoutTrigger.AfterIdle,
                    Minutes = constraint.Minutes,
                },
        MaximumVaultTimeoutMode.OnSystemLock =>
            preference.Trigger == VaultTimeoutTrigger.AfterIdle
                ? preference
                : preference with
                {
                    Trigger = VaultTimeoutTrigger.OnSystemLock,
                    Minutes = 0,
                },
        MaximumVaultTimeoutMode.OnAppRestart =>
            preference.Trigger is VaultTimeoutTrigger.AfterIdle
                or VaultTimeoutTrigger.OnSystemLock
                or VaultTimeoutTrigger.OnAppRestart
                ? preference
                : preference with
                {
                    Trigger = VaultTimeoutTrigger.OnAppRestart,
                    Minutes = 0,
                },
        _ => throw new ArgumentOutOfRangeException(nameof(constraint)),
    };

    private static int? IndexForExactMinutes(int minutes) => minutes switch
    {
        1 => 1,
        5 => 2,
        15 => 3,
        30 => 4,
        60 => 5,
        240 => 6,
        _ => null,
    };
}
