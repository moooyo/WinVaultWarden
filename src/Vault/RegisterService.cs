using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

public sealed class RegisterService : IRegisterService
{
    private const int Iterations = 600_000;
    private readonly CryptoService _crypto;
    private readonly IVaultApiClientProvider _clients;

    public RegisterService(CryptoService crypto, IVaultApiClientProvider clients)
    {
        _crypto = crypto;
        _clients = clients;
    }

    public async Task RegisterAsync(
        string serverUrl,
        string email,
        string? name,
        string password,
        string? hint,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var origin = new ServerOrigin(serverUrl);

        var masterKey = _crypto.DeriveMasterKey(password, email, KdfType.Pbkdf2, Iterations, null, null);
        var userKeyBytes = RandomNumberGenerator.GetBytes(64);
        // 提前构造:64 字节构造下 EncKey/MacKey 是 userKeyBytes 的独立拷贝,
        // 故须 Dispose 整个 userKey(清 FullKey+EncKey+MacKey 三份),仅清 userKeyBytes 会漏掉两份拷贝。
        var userKey = new SymmetricCryptoKey(userKeyBytes);
        try
        {
            var passwordHash = _crypto.ComputeMasterPasswordHash(masterKey, password);

            // Key = ProtectUserKey: stretched master key wraps UserKey
            var key = _crypto.ProtectUserKey(masterKey, userKey).ToString();

            using var rsa = RSA.Create(2048);
            var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

            // EncryptedPrivateKey = Encrypt(PKCS8 private key bytes, RAW UserKey)
            // 导出的 PKCS8 明文私钥用完(已拷进密文)立即清零,避免临时数组驻留内存。
            var pkcs8 = rsa.ExportPkcs8PrivateKey();
            string encryptedPrivateKey;
            try
            {
                encryptedPrivateKey = _crypto.Encrypt(pkcs8, userKey).ToString();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }

            var req = new RegisterRequest(
                email,
                string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                passwordHash,
                string.IsNullOrEmpty(hint) ? null : hint,
                key,
                0,
                Iterations,
                null,
                null,
                new RegisterKeys(encryptedPrivateKey, publicKey));

            try
            {
                var api = _clients.GetAnonymous<IRegistrationApiClient>(origin);
                await api.RegisterAsync(req, ct);
            }
            catch (VaultWriteException ex)
            {
                throw new RegistrationException(ex.Message);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            // 清零 UserKey 全部支撑数组(含 FullKey==userKeyBytes 及 EncKey/MacKey 拷贝)。
            userKey.Dispose();
        }
    }
}
