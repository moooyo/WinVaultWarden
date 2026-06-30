using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Services;
using Crypto;

namespace Vault;

public sealed class RegisterService : IRegisterService
{
    private const int Iterations = 600_000;
    private readonly CryptoService _crypto;
    private readonly IAccountApiClient _api;

    public RegisterService(CryptoService crypto, IAccountApiClient api)
    {
        _crypto = crypto;
        _api = api;
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
        _api.SetBaseAddress(serverUrl.TrimEnd('/'));

        var masterKey = _crypto.DeriveMasterKey(password, email, KdfType.Pbkdf2, Iterations, null, null);
        var userKeyBytes = RandomNumberGenerator.GetBytes(64);
        try
        {
            var passwordHash = _crypto.ComputeMasterPasswordHash(masterKey, password);
            var userKey = new SymmetricCryptoKey(userKeyBytes);

            // Key = ProtectUserKey: stretched master key wraps UserKey
            var key = _crypto.ProtectUserKey(masterKey, userKey).ToString();

            using var rsa = RSA.Create(2048);
            var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

            // EncryptedPrivateKey = Encrypt(PKCS8 private key bytes, RAW UserKey)
            var encryptedPrivateKey = _crypto.Encrypt(rsa.ExportPkcs8PrivateKey(), userKey).ToString();

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
                await _api.RegisterAsync(req, ct);
            }
            catch (VaultWriteException ex)
            {
                throw new RegistrationException(ex.Message);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            CryptographicOperations.ZeroMemory(userKeyBytes);
        }
    }
}
