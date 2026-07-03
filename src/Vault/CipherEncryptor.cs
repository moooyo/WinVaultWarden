using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Api.Dtos;
using Core.Models;
using Crypto;

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

    public CipherEncryptor(CryptoService crypto) => _crypto = crypto;

    public CipherRequest Encrypt(Cipher cipher, SymmetricCryptoKey userKey)
    {
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
                ownedKey = _crypto.DecryptItemKey(cipher.Key, userKey);
                itemKey = ownedKey;
                wrappedKey = cipher.Key;
            }
            else if (string.IsNullOrEmpty(cipher.Id))
            {
                // 新建条目:生成随机每条目密钥。
                ownedKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
                itemKey = ownedKey;
                wrappedKey = _crypto.Encrypt(ownedKey.FullKey, userKey).ToString();
            }
            else
            {
                // 旧格式既有条目:字段沿用 UserKey,不引入每条目密钥(保持与既有附件一致)。
                itemKey = userKey;
                wrappedKey = null;
            }

            return new CipherRequest
            {
                Type = (int)cipher.Type,
                Name = EncRequired(cipher.Name, itemKey),
                Notes = Enc(cipher.Notes, itemKey),
                FolderId = string.IsNullOrEmpty(cipher.FolderId) ? null : cipher.FolderId,
                OrganizationId = cipher.OrganizationId,
                Key = wrappedKey,
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt ? 1 : 0,
                Fields = EncFields(cipher.Fields, itemKey),
                Login = cipher.Login is null ? null : EncLogin(cipher.Login, itemKey),
                Card = cipher.Card is null ? null : EncCard(cipher.Card, itemKey),
                Identity = cipher.Identity is null ? null : EncIdentity(cipher.Identity, itemKey),
                SecureNote = cipher.SecureNote is null ? null : new SecureNoteRequest { Type = cipher.SecureNote.Type },
                SshKey = cipher.Ssh is null ? null : EncSsh(cipher.Ssh, itemKey),
                LastKnownRevisionDate = cipher.RevisionDate == default
                    ? null
                    : cipher.RevisionDate.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                PasswordHistory = EncPasswordHistory(cipher.PasswordHistory, itemKey),
            };
        }
        finally
        {
            ownedKey?.Dispose();
        }
    }

    public string EncryptFolderName(string name, SymmetricCryptoKey userKey) => EncRequired(name, userKey);

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
        Totp = Enc(login.Totp, key),
        Uris = login.Uris.Count == 0
            ? null
            : login.Uris.Select(u => new LoginUriRequest { Uri = Enc(u.Uri, key), Match = u.Match }).ToArray(),
        Fido2Credentials = login.Fido2Credentials.Count == 0
            ? null
            : login.Fido2Credentials.Select(c => EncFido2(c, key)).ToArray(),
    };

    private Fido2CredentialRequest EncFido2(CipherFido2Credential c, SymmetricCryptoKey key) => new()
    {
        CredentialId = Enc(c.CredentialId, key),
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

    private FieldRequest[]? EncFields(IReadOnlyList<CipherField> fields, SymmetricCryptoKey key) =>
        fields.Count == 0
            ? null
            : fields.Select(f => new FieldRequest { Type = (int)f.Type, Name = Enc(f.Name, key), Value = Enc(f.Value, key) }).ToArray();

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
