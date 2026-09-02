using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Api.Dtos;
using Core.Models;
using Core.Passkeys;
using Core.Services;
using Crypto;
using Crypto.V2;

namespace Vault;

// 把解密后的领域 Cipher / 文件夹名编码成加密的写入请求。
// Bitwarden 每条目密钥模型:
//   - 新建条目:生成随机 64 字节 Cipher Key,用它加密所有字段,再用 UserKey 包裹放入 cipher.key;
//   - 编辑既有条目:必须**复用**服务端原下发的 Cipher Key(cipher.Key 非空),原样回发——
//     绝不轮换,否则该条目附件的 akey(包裹在旧条目密钥下)会失配、导致附件永久不可解(数据丢失);
//   - 旧格式既有条目(无每条目密钥):字段沿用 UserKey 加密,Key 保持 null,不引入新条目密钥。
// 文件夹无每项密钥,名称直接用 UserKey 加密。须与 VaultDecryptor 严格互逆。
public sealed class CipherEncryptor
{
    private readonly CryptoService _crypto;
    private readonly CipherBlobCodec _blob;

    public CipherEncryptor(CryptoService crypto)
    {
        _crypto = crypto;
        _blob = new CipherBlobCodec(crypto);
    }

    public CipherRequest Encrypt(Cipher cipher, SymmetricCryptoKey wrappingKey) =>
        Encrypt(cipher, wrappingKey, VaultCapabilities.None);

    public CipherRequest Encrypt(
        Cipher cipher,
        SymmetricCryptoKey wrappingKey,
        VaultCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        ArgumentNullException.ThrowIfNull(wrappingKey);
        ArgumentNullException.ThrowIfNull(capabilities);
        CipherWritePolicy.EnsureCanWrite(cipher, capabilities);
        if (cipher.UsesBlobEncryption)
        {
            throw new InvalidDataException(
                "A blob-encrypted cipher cannot be downgraded to legacy field encryption.");
        }

        // 决定用于加密字段的条目有效密钥(itemKey)与要回发的 cipher.key。
        // ownedKey:本方法自行创建、需在 finally 清零的密钥(复用解出的 / 新生成的);
        // 旧格式分支下 itemKey 别名 userKey,不属本方法所有,不得释放。
        SymmetricCryptoKey? ownedKey = null;
        try
        {
            SymmetricCryptoKey itemKey;
            string? wrappedKey;
            if (!string.IsNullOrEmpty(cipher.Key))
            {
                // 编辑既有条目:复用原条目密钥,原样回发,不轮换。
                ownedKey = _crypto.DecryptItemKey(cipher.Key, wrappingKey);
                itemKey = ownedKey;
                wrappedKey = cipher.Key;
            }
            else if (string.IsNullOrEmpty(cipher.Id))
            {
                // 新建条目:生成随机每条目密钥。
                ownedKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
                itemKey = ownedKey;
                wrappedKey = _crypto.Encrypt(ownedKey.FullKey, wrappingKey).ToString();
            }
            else
            {
                // 旧格式既有条目:字段沿用 UserKey,不引入每条目密钥(保持与既有附件一致)。
                itemKey = wrappingKey;
                wrappedKey = null;
            }

            return EncryptRequest(cipher, itemKey, wrappedKey, cipher.OrganizationId);
        }
        finally
        {
            ownedKey?.Dispose();
        }
    }

    public CipherRequest Encrypt(
        Cipher cipher,
        V2UserKey userKey,
        VaultCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        ArgumentNullException.ThrowIfNull(userKey);
        ArgumentNullException.ThrowIfNull(capabilities);
        CipherWritePolicy.EnsureCanWrite(cipher, capabilities);
        if (!string.IsNullOrWhiteSpace(cipher.OrganizationId))
        {
            throw new InvalidDataException(
                "Organization ciphers do not use personal-vault blob encryption.");
        }

        SymmetricCryptoKey? itemKey = null;
        byte[]? itemKeyBytes = null;
        try
        {
            string wrappedKey;
            if (!string.IsNullOrWhiteSpace(cipher.Key))
            {
                itemKeyBytes = userKey.Decrypt(
                    cipher.Key,
                    CoseContentFormat.BitwardenLegacyKey);
                if (itemKeyBytes.Length != 64)
                    throw new CryptographicException(
                        "A V2 cipher key must contain a 64-byte AES-CBC-HMAC key.");
                itemKey = new SymmetricCryptoKey(itemKeyBytes);
                itemKeyBytes = null;
                wrappedKey = cipher.Key;
            }
            else if (string.IsNullOrEmpty(cipher.Id))
            {
                itemKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
                wrappedKey = userKey.Encrypt(
                    itemKey.FullKey,
                    CoseContentFormat.BitwardenLegacyKey);
            }
            else
            {
                throw new CryptographicException(
                    "An existing V2 personal cipher without an item key cannot be safely rewritten.");
            }

            return new CipherRequest
            {
                Type = (int)cipher.Type,
                EncryptedByKeyId = userKey.KeyIdHex,
                Name = null,
                Notes = null,
                FolderId = string.IsNullOrEmpty(cipher.FolderId) ? null : cipher.FolderId,
                OrganizationId = null,
                Key = wrappedKey,
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt ? 1 : 0,
                ArchivedDate = cipher.ArchivedDate,
                LastKnownRevisionDate = cipher.RevisionDate == default
                    ? null
                    : cipher.RevisionDate.ToUniversalTime().ToString(
                        "o",
                        CultureInfo.InvariantCulture),
                Data = _blob.Encrypt(cipher, itemKey),
            };
        }
        finally
        {
            if (itemKeyBytes is not null)
                CryptographicOperations.ZeroMemory(itemKeyBytes);
            itemKey?.Dispose();
        }
    }

    public CipherRequest EncryptForOrganizationTransfer(
        Cipher cipher,
        SymmetricCryptoKey userKey,
        SymmetricCryptoKey organizationKey,
        string organizationId) =>
        EncryptForOrganizationTransfer(
            cipher,
            userKey,
            organizationKey,
            organizationId,
            VaultCapabilities.None);

    public CipherRequest EncryptForOrganizationTransfer(
        Cipher cipher,
        SymmetricCryptoKey userKey,
        SymmetricCryptoKey organizationKey,
        string organizationId,
        VaultCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        ArgumentNullException.ThrowIfNull(userKey);
        ArgumentNullException.ThrowIfNull(organizationKey);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (string.IsNullOrWhiteSpace(organizationId))
            throw new ArgumentException("The target organization ID is required.", nameof(organizationId));
        if (string.IsNullOrWhiteSpace(cipher.Id))
            throw new OrganizationCipherOperationException("Only an existing personal cipher can be transferred.");
        if (!string.IsNullOrWhiteSpace(cipher.OrganizationId))
            throw new OrganizationCipherOperationException("Only a personal cipher can be transferred to an organization.");

        CipherWritePolicy.EnsureCanWrite(cipher, capabilities);

        if (string.IsNullOrEmpty(cipher.Key))
        {
            if (cipher.Attachments.Count > 0)
                throw new LegacyCipherAttachmentTransferException(cipher.Id);

            return EncryptRequest(cipher, organizationKey, wrappedKey: null, organizationId);
        }

        using var itemKey = _crypto.DecryptItemKey(cipher.Key, userKey);
        var wrappedKey = _crypto.Encrypt(itemKey.FullKey, organizationKey).ToString();
        return EncryptRequest(cipher, itemKey, wrappedKey, organizationId);
    }

    public CipherRequest EncryptForOrganizationTransfer(
        Cipher cipher,
        V2UserKey userKey,
        SymmetricCryptoKey organizationKey,
        string organizationId,
        VaultCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        ArgumentNullException.ThrowIfNull(userKey);
        ArgumentNullException.ThrowIfNull(organizationKey);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (string.IsNullOrWhiteSpace(organizationId))
            throw new ArgumentException("The target organization ID is required.", nameof(organizationId));
        if (string.IsNullOrWhiteSpace(cipher.Id)
            || !string.IsNullOrWhiteSpace(cipher.OrganizationId))
        {
            throw new OrganizationCipherOperationException(
                "Only an existing personal cipher can be transferred to an organization.");
        }
        if (string.IsNullOrWhiteSpace(cipher.Key))
        {
            throw new OrganizationCipherOperationException(
                "A V2 personal cipher without an item key cannot be transferred safely.");
        }

        CipherWritePolicy.EnsureCanWrite(cipher, capabilities);
        var itemKeyBytes = userKey.Decrypt(
            cipher.Key,
            CoseContentFormat.BitwardenLegacyKey);
        try
        {
            if (itemKeyBytes.Length != 64)
                throw new CryptographicException(
                    "A V2 cipher key must contain a 64-byte AES-CBC-HMAC key.");
            using var itemKey = new SymmetricCryptoKey(itemKeyBytes);
            itemKeyBytes = Array.Empty<byte>();
            var wrappedKey = _crypto.Encrypt(itemKey.FullKey, organizationKey).ToString();
            return EncryptRequest(cipher, itemKey, wrappedKey, organizationId);
        }
        finally
        {
            if (itemKeyBytes.Length > 0)
                CryptographicOperations.ZeroMemory(itemKeyBytes);
        }
    }

    public string EncryptFolderName(string name, SymmetricCryptoKey userKey) => EncRequired(name, userKey);

    public string EncryptFolderName(string name, V2UserKey userKey)
    {
        ArgumentNullException.ThrowIfNull(userKey);
        var buffer = Encoding.UTF8.GetBytes(name ?? string.Empty);
        try
        {
            return userKey.Encrypt(buffer, CoseContentFormat.Utf8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private CipherRequest EncryptRequest(
        Cipher cipher,
        SymmetricCryptoKey itemKey,
        string? wrappedKey,
        string? organizationId) => new()
    {
        Type = (int)cipher.Type,
        Name = EncRequired(cipher.Name, itemKey),
        Notes = Enc(cipher.Notes, itemKey),
        FolderId = string.IsNullOrEmpty(cipher.FolderId) ? null : cipher.FolderId,
        OrganizationId = organizationId,
        Key = wrappedKey,
        Favorite = cipher.Favorite,
        Reprompt = cipher.Reprompt ? 1 : 0,
        Fields = EncFields(cipher.Fields, itemKey),
        Login = cipher.Login is null ? null : EncLogin(cipher.Login, itemKey),
        Card = cipher.Card is null ? null : EncCard(cipher.Card, itemKey),
        Identity = cipher.Identity is null ? null : EncIdentity(cipher.Identity, itemKey),
        SecureNote = cipher.SecureNote is null ? null : new SecureNoteRequest { Type = cipher.SecureNote.Type },
        SshKey = cipher.Ssh is null ? null : EncSsh(cipher.Ssh, itemKey),
        BankAccount = cipher.BankAccount is null ? null : EncBankAccount(cipher.BankAccount, itemKey),
        DriversLicense = cipher.DriversLicense is null ? null : EncDriversLicense(cipher.DriversLicense, itemKey),
        Passport = cipher.Passport is null ? null : EncPassport(cipher.Passport, itemKey),
        ArchivedDate = cipher.ArchivedDate,
        LastKnownRevisionDate = cipher.RevisionDate == default
            ? null
            : cipher.RevisionDate.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        PasswordHistory = EncPasswordHistory(cipher.PasswordHistory, itemKey),
    };

    // 空历史→省略字段(null);非空历史→完整数组。服务端用请求里的 password_history 整体覆盖,
    // 发 null 会清空既有历史,故保留编辑时必须回发非空数组。
    private PasswordHistoryRequest[]? EncPasswordHistory(IReadOnlyList<PasswordHistoryEntry> history, SymmetricCryptoKey key)
    {
        if (history.Count == 0) return null;
        return history
            .Select(h => new PasswordHistoryRequest
            {
                Password = Enc(h.Password, key),
                LastUsedDate = h.LastUsedDate?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            })
            .ToArray();
    }

    private LoginRequest EncLogin(CipherLogin login, SymmetricCryptoKey key) => new()
    {
        Username = Enc(login.Username, key),
        Password = Enc(login.Password, key),
        PasswordRevisionDate = login.PasswordRevisionDate,
        Totp = Enc(login.Totp, key),
        AutofillOnPageLoad = login.AutofillOnPageLoad,
        Uris = login.Uris.Count == 0
            ? null
            : login.Uris.Select(u => new LoginUriRequest { Uri = Enc(u.Uri, key), Match = u.Match }).ToArray(),
        Fido2Credentials = login.Fido2Credentials.Count == 0
            ? null
            : login.Fido2Credentials.Select(c => EncFido2(c, key)).ToArray(),
    };

    private Fido2CredentialRequest EncFido2(CipherFido2Credential c, SymmetricCryptoKey key) => new()
    {
        CredentialId = Enc(NormalizeCredentialIdForWrite(c.CredentialId), key),
        KeyType = Enc(c.KeyType, key),
        KeyAlgorithm = Enc(c.KeyAlgorithm, key),
        KeyCurve = Enc(c.KeyCurve, key),
        KeyValue = Enc(c.KeyValue, key),
        RpId = Enc(c.RpId, key),
        UserHandle = Enc(c.UserHandle, key),
        UserName = Enc(c.UserName, key),
        Counter = Enc((c.Counter ?? 0).ToString(CultureInfo.InvariantCulture), key),
        RpName = Enc(c.RpName, key),
        UserDisplayName = Enc(c.UserDisplayName, key),
        Discoverable = Enc(c.Discoverable ? "true" : "false", key),
        CreationDate = c.CreationDate,
    };

    private static string? NormalizeCredentialIdForWrite(string? credentialId) =>
        CredentialId.TryNormalizeStorage(credentialId, out var normalized)
            ? normalized
            : credentialId;

    private CardRequest EncCard(CipherCard card, SymmetricCryptoKey key) => new()
    {
        CardholderName = Enc(card.CardholderName, key),
        Brand = Enc(card.Brand, key),
        Number = Enc(card.Number, key),
        ExpMonth = Enc(card.ExpMonth, key),
        ExpYear = Enc(card.ExpYear, key),
        Code = Enc(card.Code, key),
    };

    private IdentityRequest EncIdentity(CipherIdentity id, SymmetricCryptoKey key) => new()
    {
        Title = Enc(id.Title, key),
        FirstName = Enc(id.FirstName, key),
        MiddleName = Enc(id.MiddleName, key),
        LastName = Enc(id.LastName, key),
        Username = Enc(id.Username, key),
        Company = Enc(id.Company, key),
        Ssn = Enc(id.Ssn, key),
        PassportNumber = Enc(id.PassportNumber, key),
        LicenseNumber = Enc(id.LicenseNumber, key),
        Email = Enc(id.Email, key),
        Phone = Enc(id.Phone, key),
        Address1 = Enc(id.Address1, key),
        Address2 = Enc(id.Address2, key),
        Address3 = Enc(id.Address3, key),
        City = Enc(id.City, key),
        State = Enc(id.State, key),
        PostalCode = Enc(id.PostalCode, key),
        Country = Enc(id.Country, key),
    };

    private SshKeyRequest EncSsh(CipherSsh ssh, SymmetricCryptoKey key) => new()
    {
        PrivateKey = Enc(ssh.PrivateKey, key),
        PublicKey = Enc(ssh.PublicKey, key),
        KeyFingerprint = Enc(ssh.Fingerprint, key),
    };

    private BankAccountRequest EncBankAccount(CipherBankAccount account, SymmetricCryptoKey key) => new()
    {
        BankName = Enc(account.BankName, key),
        NameOnAccount = Enc(account.NameOnAccount, key),
        AccountType = Enc(account.AccountType, key),
        AccountNumber = Enc(account.AccountNumber, key),
        RoutingNumber = Enc(account.RoutingNumber, key),
        BranchNumber = Enc(account.BranchNumber, key),
        Pin = Enc(account.Pin, key),
        SwiftCode = Enc(account.SwiftCode, key),
        Iban = Enc(account.Iban, key),
        BankContactPhone = Enc(account.BankContactPhone, key),
    };

    private DriversLicenseRequest EncDriversLicense(CipherDriversLicense license, SymmetricCryptoKey key) => new()
    {
        FirstName = Enc(license.FirstName, key),
        MiddleName = Enc(license.MiddleName, key),
        LastName = Enc(license.LastName, key),
        DateOfBirth = Enc(license.DateOfBirth, key),
        LicenseNumber = Enc(license.LicenseNumber, key),
        IssuingCountry = Enc(license.IssuingCountry, key),
        IssuingState = Enc(license.IssuingState, key),
        IssueDate = Enc(license.IssueDate, key),
        ExpirationDate = Enc(license.ExpirationDate, key),
        IssuingAuthority = Enc(license.IssuingAuthority, key),
        LicenseClass = Enc(license.LicenseClass, key),
    };

    private PassportRequest EncPassport(CipherPassport passport, SymmetricCryptoKey key) => new()
    {
        Surname = Enc(passport.Surname, key),
        GivenName = Enc(passport.GivenName, key),
        DateOfBirth = Enc(passport.DateOfBirth, key),
        Sex = Enc(passport.Sex, key),
        BirthPlace = Enc(passport.BirthPlace, key),
        Nationality = Enc(passport.Nationality, key),
        IssuingCountry = Enc(passport.IssuingCountry, key),
        PassportNumber = Enc(passport.PassportNumber, key),
        PassportType = Enc(passport.PassportType, key),
        NationalIdentificationNumber = Enc(passport.NationalIdentificationNumber, key),
        IssuingAuthority = Enc(passport.IssuingAuthority, key),
        IssueDate = Enc(passport.IssueDate, key),
        ExpirationDate = Enc(passport.ExpirationDate, key),
    };

    private FieldRequest[]? EncFields(IReadOnlyList<CipherField> fields, SymmetricCryptoKey key) =>
        fields.Count == 0
            ? null
            : fields.Select(f => new FieldRequest
            {
                Type = f.WireType,
                Name = Enc(f.Name, key),
                Value = Enc(f.Value, key),
                LinkedId = f.LinkedId,
            }).ToArray();

    // 明文源 string 无法清零(不可变、可能被驻留),但加密用的临时 UTF-8 字节缓冲拷进密文后立即清零,减少额外驻留。
    private string? Enc(string? value, SymmetricCryptoKey key)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        var buffer = Encoding.UTF8.GetBytes(value);
        try { return _crypto.Encrypt(buffer, key).ToString(); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private string EncRequired(string value, SymmetricCryptoKey key)
    {
        var buffer = Encoding.UTF8.GetBytes(value ?? string.Empty);
        try { return _crypto.Encrypt(buffer, key).ToString(); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
