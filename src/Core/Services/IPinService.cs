namespace Core.Services;

// PIN 解锁管理:设/清/查。SetPin 要求 vault 解锁。
public interface IPinService
{
    bool IsPinSet { get; }
    bool RequireMasterPasswordOnRestart => false;
    bool CanRequireMasterPasswordOnRestart => true;
    bool RequiresMasterPasswordBeforePin => false;
    void SetPin(string pin);
    void SetPin(string pin, bool requireMasterPasswordOnRestart)
    {
        if (requireMasterPasswordOnRestart)
            throw new NotSupportedException("This PIN service cannot configure the restart policy.");
        SetPin(pin);
    }
    void SetRequireMasterPasswordOnRestart(bool value) =>
        throw new NotSupportedException("This PIN service cannot configure the restart policy.");
    void ClearPin();
}
