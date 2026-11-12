using System.Security.Cryptography;
using Core.Enums;
using Core.Models;
using Core.Session;
using Core.SshAgent;

namespace Vault;

/// <summary>Exposes public metadata and approves each use of a vault-owned SSH key.</summary>
public sealed class SshAgentService(
    VaultSession session,
    ISshAgentApprovalService approval,
    TimeSpan? approvalTimeout = null) : ISshAgentRequestHandler
{
    private readonly SemaphoreSlim _approvalGate = new(1, 1);
    private readonly TimeSpan _approvalTimeout = approvalTimeout ?? TimeSpan.FromMinutes(2);

    public Task<IReadOnlyList<SshAgentIdentity>> GetIdentitiesAsync(
        ISshAgentClientIdentity client,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ct.ThrowIfCancellationRequested();
        if (!client.IsCurrent || !TryCaptureUnlocked(out var lease))
            return Task.FromResult<IReadOnlyList<SshAgentIdentity>>([]);
        var candidates = Candidates(lease);
        session.ThrowIfExpired(lease);
        return Task.FromResult<IReadOnlyList<SshAgentIdentity>>(candidates
            .Select(candidate => new SshAgentIdentity(candidate.Public.Blob, candidate.Cipher.Name))
            .ToArray());
    }

    public async Task<byte[]?> SignAsync(
        ISshAgentClientIdentity client,
        byte[] publicKeyBlob,
        byte[] data,
        uint flags,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(publicKeyBlob);
        ArgumentNullException.ThrowIfNull(data);
        ct.ThrowIfCancellationRequested();
        if (publicKeyBlob.Length is 0 or > SshAgentProtocol.MaximumPublicKeyLength
            || data.Length > SshAgentKey.MaxSignedDataBytes
            || !client.IsCurrent || !TryCaptureUnlocked(out var lease))
            return null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        linked.CancelAfter(_approvalTimeout);
        if (!await _approvalGate.WaitAsync(0, linked.Token).ConfigureAwait(false))
            return null;
        try
        {
            var candidate = FindCandidate(lease, publicKeyBlob);
            if (candidate is null || !AllowedFlags(candidate.Public.Algorithm, flags))
                return null;
            var request = new SshAgentApprovalRequest(
                candidate.Cipher.Id,
                candidate.Cipher.Name,
                candidate.Public.Algorithm,
                candidate.Public.Fingerprint,
                client.Info,
                data.Length,
                "SHA256:" + Convert.ToBase64String(SHA256.HashData(data)).TrimEnd('='),
                candidate.RequiresReprompt);
            var decision = approval.ConfirmAsync(request, linked.Token);
            bool approved;
            try
            {
                approved = await decision.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                // A late OS/UI callback must never keep the agent alive or create an authorization.
                _ = decision.ContinueWith(static task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted
                        | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            if (!approved)
                return null;
            linked.Token.ThrowIfCancellationRequested();
            if (!client.IsCurrent)
                return null;

            var current = FindCandidate(lease, publicKeyBlob);
            if (current is null
                || !ReferenceEquals(current.Snapshot, candidate.Snapshot)
                || current.Cipher.Id != candidate.Cipher.Id
                || current.RequiresReprompt != candidate.RequiresReprompt)
                return null;
            // Key import and signing run outside the session lock so lock/logout never
            // wait for native cryptography. A final brief snapshot/lease check
            // rejects and clears any result produced during a lifecycle transition.
            byte[] signature;
            using (var key = SshAgentKey.Parse(current.Cipher.Ssh!.PrivateKey!, current.Cipher.Ssh.PublicKey))
            {
                linked.Token.ThrowIfCancellationRequested();
                signature = key.Sign(data, flags);
            }
            var accepted = false;
            try
            {
                session.TryCommit(lease, () =>
                {
                    accepted = !linked.IsCancellationRequested && client.IsCurrent
                        && ReferenceEquals(session.GetCiphers(lease), current.Snapshot);
                });
                if (!accepted)
                    return null;
                return signature;
            }
            finally
            {
                if (!accepted)
                    CryptographicOperations.ZeroMemory(signature);
            }
        }
        finally
        {
            _approvalGate.Release();
        }
    }

    private bool TryCaptureUnlocked(out VaultSessionLease lease)
    {
        try
        {
            lease = session.CaptureLease();
            return session.IsUnlocked(lease);
        }
        catch (InvalidOperationException)
        {
            lease = null!;
            return false;
        }
    }

    private Candidate? FindCandidate(VaultSessionLease lease, byte[] publicKey) =>
        Candidates(lease).FirstOrDefault(candidate =>
            CryptographicOperations.FixedTimeEquals(candidate.Public.Blob, publicKey));

    private IReadOnlyList<Candidate> Candidates(VaultSessionLease lease)
    {
        if (!session.IsUnlocked(lease))
            return [];
        var snapshot = session.GetCiphers(lease);
        var candidates = new List<Candidate>();
        foreach (var cipher in snapshot.OrderBy(cipher => cipher.Id, StringComparer.Ordinal))
        {
            if (cipher.Type != CipherType.SshKey || cipher.IsDeleted || cipher.IsArchived
                || !cipher.CanViewPassword || string.IsNullOrEmpty(cipher.Ssh?.PrivateKey)
                || string.IsNullOrEmpty(cipher.Ssh.PublicKey))
                continue;
            SshAgentPublicKey publicKey;
            try
            {
                publicKey = SshAgentPublicKey.Parse(cipher.Ssh.PublicKey);
            }
            catch (Exception exception) when (exception is FormatException
                or InvalidDataException or NotSupportedException or ArgumentException)
            {
                continue;
            }
            var duplicate = candidates.FindIndex(candidate =>
                candidate.Public.Blob.AsSpan().SequenceEqual(publicKey.Blob));
            if (duplicate >= 0)
            {
                // A duplicated key must retain the strongest reprompt requirement.
                if (cipher.Reprompt)
                    candidates[duplicate] = candidates[duplicate] with { RequiresReprompt = true };
                continue;
            }
            if (candidates.Count >= SshAgentProtocol.MaximumIdentityCount)
                continue;
            candidates.Add(new Candidate(cipher, publicKey, cipher.Reprompt, snapshot));
        }
        return candidates;
    }

    private static bool AllowedFlags(string algorithm, uint flags) =>
        algorithm == "ssh-rsa" ? flags is 2 or 4 : flags == 0;

    private sealed record Candidate(Cipher Cipher, SshAgentPublicKey Public, bool RequiresReprompt,
        IReadOnlyList<Cipher> Snapshot);
}
