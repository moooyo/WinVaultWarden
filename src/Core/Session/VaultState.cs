namespace Core.Session;

public enum VaultState
{
    LoggedOut,
    Locked,
    Unlocking,
    Syncing,
    Unlocked,
    Error,
}
