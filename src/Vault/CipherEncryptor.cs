using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Api.Dtos;
using Core.Models;
using Crypto;

namespace Vault;

// 把解密后的领域 Cipher / 文件夹名编码成加密的写入请求。
// 严格按 Bitwarden 每条目密钥模型:为每次写入生成随机 64 字节 Cipher Key,
// 用它加密所有字段,再用 UserKey 包裹该 Cipher Key 放入 cipher.key。
// 文件夹无每项密钥,名称直接用 UserKey 加密。须与 VaultDecryptor 严格互逆。
public sealed class CipherEncryptor
{
    private readonly CryptoService _crypto;

    public CipherEncryptor(CryptoService crypto) => _crypto = crypto;

    public CipherRequest Encrypt(Cipher cipher, SymmetricCryptoKey userKey)
    {
        var cipherKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var wrappedKey = _crypto.Encrypt(cipherKey.FullKey, userKey).ToString();

        return new CipherRequest
        {
            Type = (int)cipher.Type,
            Name = EncRequired(cipher.Name, cipherKey),
            Notes = Enc(cipher.Notes, cipherKey),
            FolderId = string.IsNullOrEmpty(cipher.FolderId) ? null : cipher.FolderId,
            OrganizationId = cipher.OrganizationId,
            Key = wrappedKey,
            Favorite = cipher.Favorite,
            Reprompt = cipher.Reprompt ? 1 : 0,
            Fields = EncFields(cipher.Fields, cipherKey),
            Login = cipher.Login is null ? null : EncLogin(cipher.Login, cipherKey),
            Card = cipher.Card is null ? null : EncCard(cipher.Card, cipherKey),
            Identity = cipher.Identity is null ? null : EncIdentity(cipher.Identity, cipherKey),
            SecureNote = cipher.SecureNote is null ? null : new SecureNoteRequest { Type = cipher.SecureNote.Type },
            SshKey = cipher.Ssh is null ? null : EncSsh(cipher.Ssh, cipherKey),
            LastKnownRevisionDate = cipher.RevisionDate == default
                ? null
                : cipher.RevisionDate.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        };
    }

    public string EncryptFolderName(string name, SymmetricCryptoKey userKey) => EncRequired(name, userKey);

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

    private string? Enc(string? value, SymmetricCryptoKey key) =>
        string.IsNullOrEmpty(value) ? null : _crypto.Encrypt(Encoding.UTF8.GetBytes(value), key).ToString();

    private string EncRequired(string value, SymmetricCryptoKey key) =>
        _crypto.Encrypt(Encoding.UTF8.GetBytes(value ?? string.Empty), key).ToString();
}
