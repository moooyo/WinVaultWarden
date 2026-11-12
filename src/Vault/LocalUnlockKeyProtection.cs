using System.Security.Cryptography;
using Core.Abstractions;
using Core.Models;
using Crypto;

namespace Vault;

/// <summary>
/// Protects either supported user-key representation with a local unlock key.
/// The authenticated local envelope carries the complete V2 COSE transport,
/// including its key identifier, rather than a legacy symmetric-key projection.
/// </summary>
internal static class LocalUnlockKeyProtection
{
    public static void RequireUserKeyBinding(VaultUserKey userKey, PersistedSession persisted)
    {
        if (userKey.SecurityVersion != persisted.UserKeySecurityVersion
            || persisted.UserKeyId is not null && persisted.UserKeyId != userKey.KeyId)
        {
            throw new InvalidDataException(
                "The unlocked key does not match the saved account encryption binding.");
        }
    }

    public static string Protect(
        CryptoService crypto,
        VaultUserKey userKey,
        SymmetricCryptoKey protectionKey)
    {
        var material = userKey.Legacy is { } legacy
            ? legacy.FullKey.ToArray()
            : userKey.V2!.ExportTransport();
        try
        {
            return crypto.Encrypt(material, protectionKey).ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    public static bool TryUpdate(
        VaultSession session,
        ITokenStore tokenStore,
        Core.Session.VaultSessionLease lease,
        Func<PersistedSession, PersistedSession> update,
        Action<PersistedSession, PersistedSession>? afterUpdate = null)
    {
        var updated = false;
        return session.TryCommit(lease, () =>
        {
            if (!tokenStore.TryLoad(out var latest) || !session.IsCurrent(lease, latest))
                return;

            var replacement = update(latest);
            tokenStore.Save(replacement);
            afterUpdate?.Invoke(latest, replacement);
            updated = true;
        }) && updated;
    }

    public static void RequireUnchangedKeyBinding(
        PersistedSession expected,
        PersistedSession latest)
    {
        if (!HasSameKeyBinding(expected, latest))
        {
            throw new InvalidOperationException(
                "The account encryption binding changed during local unlock enrollment.");
        }
    }

    public static bool HasSameKeyBinding(PersistedSession expected, PersistedSession latest) =>
        expected.UserKeySecurityVersion == latest.UserKeySecurityVersion
        && expected.UserKeyId == latest.UserKeyId
        && expected.ProtectedUserKey == latest.ProtectedUserKey
        && expected.AccountCryptographicState == latest.AccountCryptographicState
        && expected.MasterPasswordSalt == latest.MasterPasswordSalt
        && expected.Email == latest.Email
        && expected.KdfType == latest.KdfType
        && expected.KdfIterations == latest.KdfIterations
        && expected.KdfMemory == latest.KdfMemory
        && expected.KdfParallelism == latest.KdfParallelism;
}
