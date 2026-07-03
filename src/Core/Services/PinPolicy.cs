namespace Core.Services;

/// <summary>
/// PIN 快速解锁策略（安全整改 M2 层1）。最短 6 位（原 4 位),允许任意字符——
/// PIN 框接受字母数字,更长/更复杂的 PIN 提升离线暴破的 keyspace。
/// 纯策略,UI 与 PinService 双端强制(纵深)。
/// </summary>
public static class PinPolicy
{
    public const int MinLength = 6;

    public static bool IsValid(string? pin) =>
        !string.IsNullOrEmpty(pin) && pin.Length >= MinLength;
}
