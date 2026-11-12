using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Core.Abstractions;
using Core.Models;
using Core.Session;

namespace Vault;

/// <summary>
/// Holds only account/protector fingerprints for master-password verifications made
/// by this live vault session. Nothing is serialized, and a new process/session owner
/// has no verification markers. The saved PIN wrapper remains DPAPI-protected on disk;
/// this policy does not describe that wrapper as memory-only.
/// </summary>
internal static class PinUnlockSessionPolicy
{
    private static readonly ConditionalWeakTable<VaultSession, VerificationState> States = new();

    public static bool RequiresMasterPassword(VaultSession session, PersistedSession persisted) =>
        persisted.RequireMasterPasswordOnRestart
        && !string.IsNullOrEmpty(persisted.PinProtectedUserKey)
        && !HasVerification(session, persisted);

    public static bool RecordMasterPasswordVerification(
        VaultSession session,
        ITokenStore tokenStore,
        VaultSessionLease lease,
        PersistedSession verifiedRecord)
    {
        var recorded = false;
        return session.TryCommit(lease, () =>
        {
            if (!session.IsUnlocked(lease)
                || !tokenStore.TryLoad(out var latest)
                || !session.IsCurrent(lease, latest)
                || !LocalUnlockKeyProtection.HasSameKeyBinding(verifiedRecord, latest))
            {
                return;
            }
            RecordVerified(session, latest);
            recorded = true;
        }) && recorded;
    }

    public static void RecordVerified(VaultSession session, PersistedSession persisted)
    {
        var state = States.GetValue(session, static _ => new VerificationState());
        var bindingId = BindingId(persisted);
        var proof = new Verification(KeyFingerprint(persisted), PinFingerprint(persisted));
        lock (state.Gate)
        {
            // A session owner only needs to remember the bounded saved-account set.
            if (!state.Verifications.ContainsKey(bindingId)
                && state.Verifications.Count >= PersistedSessionCollection.MaximumSessions)
            {
                state.Verifications.Clear();
            }
            state.Verifications[bindingId] = proof;
        }
    }

    public static void RebindAfterPinChange(
        VaultSession session,
        PersistedSession previous,
        PersistedSession updated)
    {
        if (!States.TryGetValue(session, out var state))
            return;
        var bindingId = BindingId(previous);
        lock (state.Gate)
        {
            if (!state.Verifications.TryGetValue(bindingId, out var proof)
                || proof.KeyFingerprint != KeyFingerprint(previous)
                || proof.PinFingerprint != PinFingerprint(previous)
                || !LocalUnlockKeyProtection.HasSameKeyBinding(previous, updated))
            {
                state.Verifications.Remove(bindingId);
                return;
            }
            // Enrollment can preserve an existing proof of the same master key, but
            // cannot manufacture one for a Hello-only or freshly restarted session.
            state.Verifications[bindingId] = proof with { PinFingerprint = PinFingerprint(updated) };
        }
    }

    public static void Forget(VaultSession session, PersistedSession persisted)
    {
        if (!States.TryGetValue(session, out var state))
            return;
        lock (state.Gate)
            state.Verifications.Remove(BindingId(persisted));
    }

    public static void Clear(VaultSession session) => States.Remove(session);

    private static bool HasVerification(VaultSession session, PersistedSession persisted)
    {
        if (!States.TryGetValue(session, out var state))
            return false;
        lock (state.Gate)
        {
            return state.Verifications.TryGetValue(BindingId(persisted), out var proof)
                && proof.KeyFingerprint == KeyFingerprint(persisted)
                && proof.PinFingerprint == PinFingerprint(persisted);
        }
    }

    private static string BindingId(PersistedSession persisted) =>
        new PersistedAccountBinding(persisted.AccountId, persisted.Endpoints!).CreateIdentifier();

    private static string? PinFingerprint(PersistedSession persisted) =>
        persisted.PinProtectedUserKey is { } protector ? Hash(protector) : null;

    private static string KeyFingerprint(PersistedSession persisted)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, BindingId(persisted));
        Add(hash, persisted.ProtectedUserKey);
        Add(hash, persisted.UserKeySecurityVersion.ToString(CultureInfo.InvariantCulture));
        Add(hash, persisted.UserKeyId);
        Add(hash, persisted.MasterPasswordSalt);
        Add(hash, persisted.Email);
        Add(hash, ((int)persisted.UnlockMethod).ToString(CultureInfo.InvariantCulture));
        Add(hash, persisted.KeyConnectorUrl);
        Add(hash, persisted.HasMasterPassword?.ToString());
        Add(hash, ((int)persisted.KdfType).ToString(CultureInfo.InvariantCulture));
        Add(hash, persisted.KdfIterations.ToString(CultureInfo.InvariantCulture));
        Add(hash, persisted.KdfMemory?.ToString(CultureInfo.InvariantCulture));
        Add(hash, persisted.KdfParallelism?.ToString(CultureInfo.InvariantCulture));
        var state = persisted.AccountCryptographicState;
        Add(hash, state?.SecurityVersion.ToString(CultureInfo.InvariantCulture));
        Add(hash, state?.WrappedPrivateKey);
        Add(hash, state?.PublicKey);
        Add(hash, state?.SignedPublicKey);
        Add(hash, state?.WrappedSigningKey);
        Add(hash, state?.VerifyingKey);
        Add(hash, state?.SignedSecurityState);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Hash(string value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, value);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Add(IncrementalHash hash, string? value)
    {
        byte[] encoded = value is null ? [] : Encoding.UTF8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, value is null ? -1 : encoded.Length);
            hash.AppendData(length);
            hash.AppendData(encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private sealed class VerificationState
    {
        public object Gate { get; } = new();
        public Dictionary<string, Verification> Verifications { get; } = new(StringComparer.Ordinal);
    }

    private sealed record Verification(string KeyFingerprint, string? PinFingerprint);
}
