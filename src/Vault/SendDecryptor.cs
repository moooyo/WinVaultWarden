using Api.Dtos;
using Core.Enums;
using Core.Models;
using Crypto;
using Crypto.V2;
using System.Security.Cryptography;

namespace Vault;

/// <summary>
/// Maps one encrypted Send response into its in-memory domain model. The same mapper is used for
/// full sync, online list refreshes, and offline cache restoration.
/// </summary>
internal sealed class SendDecryptor
{
    private readonly SendCryptoService _crypto;

    public SendDecryptor(SendCryptoService crypto)
    {
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
    }

    public Send Decrypt(
        SendResponseDto dto,
        VaultUserKey userKey,
        string sendWebUrl,
        string rawMutationWitness)
    {
        ArgumentNullException.ThrowIfNull(userKey);
        return userKey.Legacy is { } legacy
            ? Decrypt(dto, legacy, sendWebUrl, rawMutationWitness)
            : Decrypt(dto, userKey.V2!, sendWebUrl, rawMutationWitness);
    }

    public Send Decrypt(
        SendResponseDto dto,
        SymmetricCryptoKey userKey,
        string sendWebUrl,
        string rawMutationWitness)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var seed = _crypto.UnwrapSeed(
            dto.Key ?? throw new InvalidDataException("Send key is missing."),
            userKey);
        return DecryptWithSeed(dto, seed, sendWebUrl, rawMutationWitness);
    }

    public Send Decrypt(
        SendResponseDto dto,
        V2UserKey userKey,
        string sendWebUrl,
        string rawMutationWitness)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var seed = _crypto.UnwrapSeed(
            dto.Key ?? throw new InvalidDataException("Send key is missing."),
            userKey);
        return DecryptWithSeed(dto, seed, sendWebUrl, rawMutationWitness);
    }

    private Send DecryptWithSeed(
        SendResponseDto dto,
        byte[] seed,
        string sendWebUrl,
        string rawMutationWitness)
    {
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
                file = new SendFile(fileName, dto.File.Size ?? 0L, dto.File.SizeName, dto.File.Id);
            }

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
                HasPassword = authType == SendAuthType.Password || !string.IsNullOrEmpty(dto.Password),
                RevisionDate = dto.RevisionDate,
                RawMutationWitness = rawMutationWitness,
                ShareUrl = _crypto.BuildShareUrl(sendWebUrl, dto.AccessId, seed),
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
            null when hasPassword && hasEmails => SendAuthType.Unknown,
            null when hasPassword => SendAuthType.Password,
            null when hasEmails => SendAuthType.Email,
            null when dto.Type is (int)SendType.Text or (int)SendType.File => SendAuthType.None,
            _ => SendAuthType.Unknown,
        };
    }
}
