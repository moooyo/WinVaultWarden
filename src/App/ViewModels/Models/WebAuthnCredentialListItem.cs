namespace App.ViewModels.Models;

public enum WebAuthnVerificationMethod
{
    MasterPassword,
    EmailOtp,
    AuthRequestAccessCode,
}

public sealed record WebAuthnCredentialListItem(
    int Id,
    string Name,
    bool Migrated);

public sealed record WebAuthnManagementSnapshot(
    bool Enabled,
    IReadOnlyList<WebAuthnCredentialListItem> Credentials)
{
    public int GetNextCredentialId()
    {
        var usedIds = Credentials.Select(static credential => credential.Id).ToHashSet();
        for (var id = 1; id < int.MaxValue; id++)
        {
            if (!usedIds.Contains(id))
                return id;
        }

        throw new InvalidOperationException("No WebAuthn credential identifier is available.");
    }
}
