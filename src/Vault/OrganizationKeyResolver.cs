using System.Security.Cryptography;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Crypto.V2;

namespace Vault;

public sealed class OrganizationKeyResolver
{
    private readonly CryptoService _crypto;

    public OrganizationKeyResolver(CryptoService crypto) =>
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));

    public SymmetricCryptoKey Resolve(
        VaultOrganization organization,
        string? encryptedPrivateKey,
        SymmetricCryptoKey userKey)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(userKey);
        if (!organization.Enabled
            || organization.Status != 2
            || string.IsNullOrWhiteSpace(organization.EncryptedKey))
            throw Unavailable();

        byte[]? privateKey = null;
        byte[]? providerKeyBytes = null;
        byte[]? organizationKeyBytes = null;
        SymmetricCryptoKey? providerKey = null;
        try
        {
            var wrapped = EncString.Parse(organization.EncryptedKey);
            if (organization.IsProviderOrganization)
            {
                if (string.IsNullOrWhiteSpace(organization.ProviderId)
                    || string.IsNullOrWhiteSpace(encryptedPrivateKey)
                    || string.IsNullOrWhiteSpace(organization.EncryptedProviderKey)
                    || wrapped.Type != EncryptionType.AesCbc256_HmacSha256_B64)
                {
                    throw Unavailable();
                }

                var privateKeyEnvelope = EncString.Parse(encryptedPrivateKey);
                if (privateKeyEnvelope.Type != EncryptionType.AesCbc256_HmacSha256_B64)
                    throw Unavailable();
                privateKey = _crypto.Decrypt(privateKeyEnvelope, userKey);

                var providerKeyEnvelope = EncString.Parse(organization.EncryptedProviderKey);
                if (!providerKeyEnvelope.Type.IsRsaEnvelope())
                {
                    throw Unavailable();
                }

                providerKeyBytes = _crypto.DecryptRsa(providerKeyEnvelope, privateKey);
                if (providerKeyBytes.Length != 64)
                    throw Unavailable();
                providerKey = new SymmetricCryptoKey(providerKeyBytes);
                providerKeyBytes = null;
                organizationKeyBytes = _crypto.Decrypt(wrapped, providerKey);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(encryptedPrivateKey))
                    throw Unavailable();
                var privateKeyEnvelope = EncString.Parse(encryptedPrivateKey);
                if (privateKeyEnvelope.Type != EncryptionType.AesCbc256_HmacSha256_B64)
                    throw Unavailable();
                privateKey = _crypto.Decrypt(privateKeyEnvelope, userKey);
                organizationKeyBytes = DecryptV1DirectOrganizationKey(
                    _crypto,
                    wrapped,
                    privateKey);
            }

            var result = new SymmetricCryptoKey(organizationKeyBytes);
            organizationKeyBytes = null;
            return result;
        }
        catch (OrganizationCipherOperationException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is CryptographicException
                or FormatException
                or ArgumentException
                or InvalidOperationException)
        {
            throw new OrganizationCipherOperationException(
                "The organization key could not be resolved for this item.",
                ex);
        }
        finally
        {
            providerKey?.Dispose();
            if (privateKey is not null)
                CryptographicOperations.ZeroMemory(privateKey);
            if (providerKeyBytes is not null)
                CryptographicOperations.ZeroMemory(providerKeyBytes);
            if (organizationKeyBytes is not null)
                CryptographicOperations.ZeroMemory(organizationKeyBytes);
        }
    }

    public SymmetricCryptoKey Resolve(
        VaultOrganization organization,
        string? encryptedPrivateKey,
        V2UserKey userKey)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(userKey);
        if (!organization.Enabled
            || organization.Status != 2
            || string.IsNullOrWhiteSpace(organization.EncryptedKey))
        {
            throw Unavailable();
        }

        byte[]? privateKey = null;
        byte[]? providerKeyBytes = null;
        byte[]? organizationKeyBytes = null;
        SymmetricCryptoKey? providerKey = null;
        try
        {
            var encryptedOrganizationKey = organization.EncryptedKey;
            if (organization.IsProviderOrganization)
            {
                if (string.IsNullOrWhiteSpace(organization.ProviderId)
                    || string.IsNullOrWhiteSpace(encryptedPrivateKey)
                    || string.IsNullOrWhiteSpace(organization.EncryptedProviderKey))
                {
                    throw Unavailable();
                }

                privateKey = userKey.Decrypt(
                    encryptedPrivateKey,
                    CoseContentFormat.Pkcs8PrivateKey);
                var providerKeyEnvelope = EncString.Parse(organization.EncryptedProviderKey);
                if (!providerKeyEnvelope.Type.IsRsaEnvelope())
                    throw Unavailable();
                providerKeyBytes = _crypto.DecryptRsa(providerKeyEnvelope, privateKey);
                if (providerKeyBytes.Length != 64)
                    throw Unavailable();
                providerKey = new SymmetricCryptoKey(providerKeyBytes);
                providerKeyBytes = null;

                var wrapped = EncString.Parse(encryptedOrganizationKey);
                if (wrapped.Type != EncryptionType.AesCbc256_HmacSha256_B64)
                    throw Unavailable();
                organizationKeyBytes = _crypto.Decrypt(wrapped, providerKey);
            }
            else if (encryptedOrganizationKey.StartsWith("7.", StringComparison.Ordinal))
            {
                organizationKeyBytes = userKey.Decrypt(
                    encryptedOrganizationKey,
                    CoseContentFormat.BitwardenLegacyKey);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(encryptedPrivateKey))
                    throw Unavailable();
                var wrapped = EncString.Parse(encryptedOrganizationKey);
                if (!wrapped.Type.IsRsaEnvelope())
                    throw Unavailable();
                privateKey = userKey.Decrypt(
                    encryptedPrivateKey,
                    CoseContentFormat.Pkcs8PrivateKey);
                organizationKeyBytes = _crypto.DecryptRsa(wrapped, privateKey);
            }

            if (organizationKeyBytes.Length is not (32 or 64))
                throw Unavailable();
            var result = new SymmetricCryptoKey(organizationKeyBytes);
            organizationKeyBytes = null;
            return result;
        }
        catch (OrganizationCipherOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            CryptographicException
                or FormatException
                or ArgumentException
                or InvalidOperationException)
        {
            throw new OrganizationCipherOperationException(
                "The organization key could not be resolved for this item.",
                exception);
        }
        finally
        {
            providerKey?.Dispose();
            if (privateKey is not null)
                CryptographicOperations.ZeroMemory(privateKey);
            if (providerKeyBytes is not null)
                CryptographicOperations.ZeroMemory(providerKeyBytes);
            if (organizationKeyBytes is not null)
                CryptographicOperations.ZeroMemory(organizationKeyBytes);
        }
    }

    internal static byte[] DecryptV1DirectOrganizationKey(
        CryptoService crypto,
        EncString encryptedOrganizationKey,
        byte[] privateKey)
    {
        ArgumentNullException.ThrowIfNull(crypto);
        ArgumentNullException.ThrowIfNull(encryptedOrganizationKey);
        ArgumentNullException.ThrowIfNull(privateKey);
        if (!encryptedOrganizationKey.Type.IsRsaEnvelope())
            throw new CryptographicException("A V1 organization key must use an RSA envelope.");

        var keyBytes = crypto.DecryptRsa(encryptedOrganizationKey, privateKey);
        if (keyBytes.Length == 64)
            return keyBytes;

        CryptographicOperations.ZeroMemory(keyBytes);
        throw new CryptographicException(
            "A V1 organization key must contain a 64-byte AES-CBC-HMAC key.");
    }

    private static OrganizationCipherOperationException Unavailable() =>
        new("The organization key is unavailable for this item.");
}
