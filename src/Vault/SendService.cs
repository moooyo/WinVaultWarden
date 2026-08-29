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
    private readonly SendCryptoService _crypto;
    private readonly VaultSession _session;

    public SendService(IVaultApiClientProvider clients, SendCryptoService crypto, VaultSession session)
    {
        _clients = clients;
        _crypto = crypto;
        _session = session;
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
        var lease = _session.CaptureLease();
        if (!_session.TryCopyUserKey(lease, out var copiedKey))
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
                var value = Decrypt(dto, userKey, lease.Endpoints.SendWeb.Value, witness);
                observations.Add(new SendObservation(
                    dto.Id,
                    dto.RevisionDate,
                    SendDecryptStatus.Decrypted,
                    value,
                    witness));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or NullReferenceException)
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
        return new SendReadSnapshot(observations, lease, complete);
    }

    private Send Decrypt(
        SendResponseDto dto,
        SymmetricCryptoKey userKey,
        string origin,
        string rawMutationWitness)
    {
        var seed = _crypto.UnwrapSeed(dto.Key!, userKey);
        try
        {
            using var cryptoKey = _crypto.DeriveCryptoKey(seed);
            var type = SendTypePolicy.RequireSupported(dto.Type);
            var name = _crypto.DecryptField(dto.Name, cryptoKey) ?? string.Empty;
            var notes = _crypto.DecryptField(dto.Notes, cryptoKey);

            SendText? text = null;
            if (dto.Text is not null)
                text = new SendText(_crypto.DecryptField(dto.Text.Text, cryptoKey), dto.Text.Hidden);

            SendFile? file = null;
            if (dto.File is not null)
            {
                var fileName = _crypto.DecryptField(dto.File.FileName, cryptoKey) ?? string.Empty;
                var size = dto.File.Size ?? 0L;
                file = new SendFile(fileName, size, dto.File.SizeName, dto.File.Id);
            }

            var shareUrl = _crypto.BuildShareUrl(origin, dto.AccessId, seed);
            var authType = ResolveAuthType(dto);

            return new Send
            {
                Id = dto.Id,
                AccessId = dto.AccessId,
                Type = type,
                Name = name,
                Notes = notes,
                Text = text,
                File = file,
                MaxAccessCount = dto.MaxAccessCount,
                AccessCount = dto.AccessCount,
                ExpirationDate = dto.ExpirationDate,
                DeletionDate = dto.DeletionDate,
                Disabled = dto.Disabled,
                HideEmail = dto.HideEmail,
                AuthType = authType,
                RawAuthType = dto.AuthType ?? int.MinValue,
                // The raw password value is a server-side verifier. Keep only its protection signal.
                HasPassword = authType == SendAuthType.Password || !string.IsNullOrEmpty(dto.Password),
                RevisionDate = dto.RevisionDate,
                RawMutationWitness = rawMutationWitness,
                ShareUrl = shareUrl,
                Key = dto.Key,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    private static SendAuthType ResolveAuthType(SendResponseDto dto)
    {
        var hasPassword = !string.IsNullOrWhiteSpace(dto.Password);
        var hasEmails = !string.IsNullOrWhiteSpace(dto.Emails);
        return dto.AuthType switch
        {
            (int)SendAuthType.Email when hasPassword => SendAuthType.Unknown,
            (int)SendAuthType.Email => SendAuthType.Email,
            (int)SendAuthType.Password when hasEmails => SendAuthType.Unknown,
            (int)SendAuthType.Password => SendAuthType.Password,
            (int)SendAuthType.None when hasPassword || hasEmails => SendAuthType.Unknown,
            (int)SendAuthType.None => SendAuthType.None,
            // Legacy Vaultwarden omits authType. The complete legacy evidence matches
            // Bitwarden SendUtilities.InferAuthType while contradictory evidence fails closed.
            null when hasPassword && hasEmails => SendAuthType.Unknown,
            null when hasPassword => SendAuthType.Password,
            null when hasEmails => SendAuthType.Email,
            null when dto.Type is (int)SendType.Text or (int)SendType.File => SendAuthType.None,
            _ => SendAuthType.Unknown,
        };
    }
}
