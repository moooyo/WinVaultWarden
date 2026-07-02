namespace App.Services;

public enum VaultTimeoutAction { Lock, Logout }

public static class IdleTimeoutPolicy
{
    public static bool IsExpired(DateTimeOffset lastActivity, DateTimeOffset now, int minutes) =>
        minutes > 0 && now - lastActivity >= TimeSpan.FromMinutes(minutes);

    public static int MinutesForIndex(int index) => index switch
    {
        1 => 1, 2 => 5, 3 => 15, 4 => 30, 5 => 60, 6 => 240,
        _ => 0,   // 0(重启时) / 7(永不) / 越界 → 无空闲锁
    };

    public static VaultTimeoutAction ActionForIndex(int index) =>
        index == 1 ? VaultTimeoutAction.Logout : VaultTimeoutAction.Lock;
}
