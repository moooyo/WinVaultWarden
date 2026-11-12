namespace Core.Passkeys;

/// <summary>
/// Backup eligibility is immutable for a registered credential. New credentials use the
/// UUID storage form used by Bitwarden and advertise its synchronized-credential flags.
/// Earlier WinVaultWarden releases created opaque base64 IDs with BE/BS clear; keep those
/// flags stable instead of silently invalidating the relying party's registration.
/// </summary>
public readonly record struct PasskeyBackupState(bool Eligible, bool BackedUp)
{
    public static PasskeyBackupState Synced => new(true, true);
    public static PasskeyBackupState LegacyDeviceBound => new(false, false);

    public static PasskeyBackupState ForStoredCredential(string credentialId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        CredentialId.DecodeStorage(credentialId);
        return Guid.TryParseExact(credentialId, "D", out _)
            ? Synced
            : LegacyDeviceBound;
    }

    public void Validate()
    {
        if (BackedUp && !Eligible)
            throw new ArgumentException("Backup state requires backup eligibility.");
    }
}
