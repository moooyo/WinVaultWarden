namespace Core.Services;

// PIN 解锁管理:设/清/查。SetPin 要求 vault 解锁。
public interface IPinService
{
    bool IsPinSet { get; }
    void SetPin(string pin);
    void ClearPin();
}
