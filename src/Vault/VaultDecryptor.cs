using System.Security.Cryptography;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Crypto;

namespace Vault;

public sealed record DecryptedVault(
    AccountInfo Account,
    IReadOnlyList<Folder> Folders,
    IReadOnlyList<Cipher> Ciphers,
    int SkippedCipherCount);

public sealed class VaultDecryptor
{
    private readonly CryptoService _crypto;

    public VaultDecryptor(CryptoService crypto) => _crypto = crypto;

    public DecryptedVault Decrypt(SyncResponse sync, SymmetricCryptoKey userKey, string serverUrl)
    {
        var account = BuildAccount(sync.Profile, serverUrl);
        var folders = (sync.Folders ?? Array.Empty<FolderDto>())
            .Select(folder => DecryptFolder(folder, userKey))
            .ToArray();

        var ciphers = new List<Cipher>();
        var skipped = 0;
        foreach (var cipher in sync.Ciphers ?? Array.Empty<CipherDto>())
        {
            try
            {
                ciphers.Add(DecryptCipher(cipher, userKey));
            }
            catch (Exception ex) when (IsCipherDecryptException(ex))
            {
                skipped++;
            }
        }

        return new DecryptedVault(account, folders, ciphers, skipped);
    }

    public Folder DecryptFolder(FolderDto folder, SymmetricCryptoKey userKey) => new()
    {
        Id = folder.Id,
        Name = Dec(folder.Name, userKey) ?? string.Empty,
        RevisionDate = folder.RevisionDate ?? DateTimeOffset.MinValue,
    };

    public Cipher DecryptCipher(CipherDto dto, SymmetricCryptoKey userKey)
    {
        var key = ResolveItemKey(dto, userKey);
        var type = (CipherType)dto.Type;

        return new Cipher
        {
            Id = dto.Id,
            Type = type,
            OrganizationId = dto.OrganizationId,
            FolderId = dto.FolderId,
            Favorite = dto.Favorite,
            Reprompt = dto.Reprompt == 1,
            Name = Dec(dto.Name, key) ?? string.Empty,
            Notes = Dec(dto.Notes, key),
            CreationDate = dto.CreationDate ?? DateTimeOffset.MinValue,
            RevisionDate = dto.RevisionDate ?? DateTimeOffset.MinValue,
            DeletedDate = dto.DeletedDate,
            Login = dto.Login is null ? null : DecryptLogin(dto.Login, key),
            Card = dto.Card is null ? null : DecryptCard(dto.Card, key),
            Identity = dto.Identity is null ? null : DecryptIdentity(dto.Identity, key),
            SecureNote = dto.SecureNote is null ? null : new CipherSecureNote(dto.SecureNote.Type),
            Ssh = dto.SshKey is null ? null : DecryptSsh(dto.SshKey, key),
            Fields = DecryptFields(dto.Fields, key),
            Attachments = DecryptAttachments(dto.Attachments, key),
        };
    }

    // 解析条目有效密钥:dto.Key 为空则用 userKey,否则解出条目级密钥。
    public SymmetricCryptoKey ResolveItemKey(CipherDto dto, SymmetricCryptoKey userKey) =>
        string.IsNullOrWhiteSpace(dto.Key)
            ? userKey
            : _crypto.DecryptItemKey(dto.Key, userKey);

    // 解密附件元数据。新格式:附件独立密钥 attKey = DecryptItemKey(d.Key, itemKey);
    // 旧格式(d.Key 为空):直接用 itemKey 解 fileName。单附件失败只跳过自身,不连累整条目。
    public IReadOnlyList<CipherAttachment> DecryptAttachments(CipherAttachmentDto[]? dtos, SymmetricCryptoKey itemKey)
    {
        if (dtos is null || dtos.Length == 0)
            return Array.Empty<CipherAttachment>();

        var result = new List<CipherAttachment>(dtos.Length);
        foreach (var d in dtos)
        {
            try
            {
                var attKey = string.IsNullOrEmpty(d.Key)
                    ? itemKey
                    : _crypto.DecryptItemKey(d.Key, itemKey);
                var fileName = _crypto.DecryptToString(d.FileName, attKey) ?? string.Empty;
                var size = long.TryParse(d.Size, out var s) ? s : 0L;
                var sizeName = d.SizeName ?? string.Empty;
                result.Add(new CipherAttachment(d.Id, fileName, size, sizeName));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                // 跳过坏附件,保留同条目其余附件。
            }
        }

        return result;
    }

    private CipherLogin DecryptLogin(LoginDto login, SymmetricCryptoKey key) => new(
            Dec(login.Username, key),
            Dec(login.Password, key),
            Dec(login.Totp, key),
            (login.Uris ?? Array.Empty<LoginUriDto>())
                .Select(uri => new CipherLoginUri(Dec(uri.Uri, key), uri.Match))
                .ToArray())
        {
            Fido2Credentials = DecryptFido2Credentials(login.Fido2Credentials, key),
        };

    private IReadOnlyList<CipherFido2Credential> DecryptFido2Credentials(Fido2CredentialDto[]? credentials, SymmetricCryptoKey key) =>
        (credentials ?? Array.Empty<Fido2CredentialDto>())
            .Select(credential =>
            {
                var counter = Dec(credential.Counter, key);
                var discoverable = Dec(credential.Discoverable, key);
                return new CipherFido2Credential(
                    Dec(credential.CredentialId, key),
                    Dec(credential.KeyType, key),
                    Dec(credential.KeyAlgorithm, key),
                    Dec(credential.KeyCurve, key),
                    Dec(credential.KeyValue, key),
                    Dec(credential.RpId, key),
                    Dec(credential.UserHandle, key),
                    Dec(credential.UserName, key),
                    TryParseCounter(counter),
                    Dec(credential.RpName, key),
                    Dec(credential.UserDisplayName, key),
                    string.Equals(discoverable, "true", StringComparison.OrdinalIgnoreCase),
                    credential.CreationDate);
            })
            .ToArray();

    private CipherCard DecryptCard(CardDto card, SymmetricCryptoKey key) => new(
        Dec(card.CardholderName, key),
        Dec(card.Number, key),
        Dec(card.ExpMonth, key),
        Dec(card.ExpYear, key),
        Dec(card.Code, key),
        Dec(card.Brand, key));

    private CipherIdentity DecryptIdentity(IdentityDto identity, SymmetricCryptoKey key) => new(
        Dec(identity.Title, key),
        Dec(identity.FirstName, key),
        Dec(identity.MiddleName, key),
        Dec(identity.LastName, key),
        Dec(identity.Username, key),
        Dec(identity.Company, key),
        Dec(identity.Ssn, key),
        Dec(identity.PassportNumber, key),
        Dec(identity.LicenseNumber, key),
        Dec(identity.Email, key),
        Dec(identity.Phone, key),
        Dec(identity.Address1, key),
        Dec(identity.Address2, key),
        Dec(identity.Address3, key),
        Dec(identity.City, key),
        Dec(identity.State, key),
        Dec(identity.PostalCode, key),
        Dec(identity.Country, key));

    private CipherSsh DecryptSsh(SshKeyDto ssh, SymmetricCryptoKey key) => new(
        Dec(ssh.PrivateKey, key),
        Dec(ssh.PublicKey, key),
        Dec(ssh.KeyFingerprint, key));

    private IReadOnlyList<CipherField> DecryptFields(FieldDto[]? fields, SymmetricCryptoKey key) =>
        (fields ?? Array.Empty<FieldDto>())
            .Select(field => new CipherField(
                Dec(field.Name, key) ?? string.Empty,
                Dec(field.Value, key),
                (CipherFieldType)field.Type))
            .ToArray();

    private static long? TryParseCounter(string? value) =>
        long.TryParse(value, out var counter) ? counter : null;

    private string? Dec(string? value, SymmetricCryptoKey key) => _crypto.DecryptToString(value, key);

    private static AccountInfo BuildAccount(ProfileDto? profile, string serverUrl)
    {
        var email = profile?.Email ?? string.Empty;
        var display = profile?.Name ?? email;
        var initial = string.IsNullOrWhiteSpace(display)
            ? string.Empty
            : display.Trim()[0].ToString().ToUpperInvariant();
        return new AccountInfo(email, serverUrl, initial, string.Empty);
    }

    private static bool IsCipherDecryptException(Exception ex) =>
        ex is CryptographicException or FormatException or ArgumentException or InvalidOperationException;
}
