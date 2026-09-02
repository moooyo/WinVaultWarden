using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

// Send 读编排:拉列表 → 逐条用 UserKey 解出 seed → 派生 cryptoKey → 解密字段 → 映射成领域 Send。
public sealed class SendService : ISendService
{
    private readonly IVaultApiClientProvider _clients;
    private readonly VaultSession _session;
    private readonly SendDecryptor _decryptor;

    public SendService(IVaultApiClientProvider clients, SendCryptoService crypto, VaultSession session)
    {
        _clients = clients;
        _session = session;
        _decryptor = new SendDecryptor(crypto);
    }

    public async Task<IReadOnlyList<Send>> GetSendsAsync(CancellationToken ct = default)
    {
        var snapshot = await ObserveSendsAsync(ct);
        return snapshot.Observations
            .Where(observation => observation.DecryptStatus == SendDecryptStatus.Decrypted)
            .Select(observation => observation.Value!)
            .ToArray();
    }

    public async Task<SendReadSnapshot> ObserveSendsAsync(CancellationToken ct = default)
    {
        VaultPolicyEvaluator.ThrowIfEnabled(
            _session,
            OrganizationPolicyType.DisableSend,
            "An organization policy disables Send.");
        var lease = _session.CaptureLease();
        if (!_session.TryGetSendSnapshot(lease, out var cachedSends, out var dataStale))
            throw new InvalidOperationException("Vault is locked.");
        if (dataStale)
        {
            return new SendReadSnapshot(
                cachedSends.Select(send => new SendObservation(
                    send.Id,
                    send.RevisionDate,
                    SendDecryptStatus.Decrypted,
                    send,
                    send.RawMutationWitness)).ToArray(),
                lease,
                IsComplete: true,
                IsStale: true);
        }

        return await RefreshSendsAsync(lease, ct).ConfigureAwait(false);
    }

    public Task<SendReadSnapshot> RefreshSendsAsync(CancellationToken ct = default)
    {
        VaultPolicyEvaluator.ThrowIfEnabled(
            _session,
            OrganizationPolicyType.DisableSend,
            "An organization policy disables Send.");
        return RefreshSendsAsync(_session.CaptureLease(), ct);
    }

    private async Task<SendReadSnapshot> RefreshSendsAsync(
        Core.Session.VaultSessionLease lease,
        CancellationToken ct)
    {
        if (!_session.TryCopyUserKeyMaterial(lease, out var copiedKey))
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey!;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ISendApiClient>(lease);
        var response = await api.GetSendsAsync(linked.Token);
        _session.ThrowIfExpired(lease);
        var rawSends = response.Data ?? Array.Empty<SendResponseDto>();
        var observations = new List<SendObservation>(rawSends.Length);
        var structurallyComplete = response.Data is not null;
        foreach (var dto in rawSends)
        {
            if (dto is null || string.IsNullOrWhiteSpace(dto.Id))
            {
                structurallyComplete = false;
                continue;
            }

            string? witness = null;
            try
            {
                witness = SendMutationWitness.Compute(dto);
                var value = _decryptor.Decrypt(
                    dto,
                    userKey,
                    lease.Endpoints.SendWeb.Value,
                    witness);
                observations.Add(new SendObservation(
                    dto.Id,
                    dto.RevisionDate,
                    SendDecryptStatus.Decrypted,
                    value,
                    witness));
            }
            catch (Exception ex) when (
                ex is CryptographicException
                    or FormatException
                    or System.Formats.Cbor.CborContentException
                    or ArgumentException
                    or InvalidDataException
                    or NullReferenceException
                    or OverflowException)
            {
                // Keep the raw identity of corrupt entries so UI filtering cannot be mistaken for
                // server-side deletion during replacement or delete reconciliation.
                observations.Add(new SendObservation(
                    dto.Id,
                    dto.RevisionDate,
                    SendDecryptStatus.Corrupt,
                    null,
                    witness));
            }
        }
        _session.ThrowIfExpired(lease);
        var complete = structurallyComplete
            && string.IsNullOrEmpty(response.ContinuationToken)
            && observations.Select(observation => observation.Id).Distinct(StringComparer.Ordinal).Count()
                == observations.Count;
        var sends = observations
            .Where(observation => observation.DecryptStatus == SendDecryptStatus.Decrypted)
            .Select(observation => observation.Value!)
            .ToArray();
        if (!_session.TrySetSends(lease, sends))
            throw new OperationCanceledException(lease.CancellationToken);
        return new SendReadSnapshot(observations, lease, complete, IsStale: false);
    }
}
