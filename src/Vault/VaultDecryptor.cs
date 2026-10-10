using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Crypto.V2;

namespace Vault;

public sealed record DecryptedVault(
    AccountInfo Account,
    IReadOnlyList<Folder> Folders,
    IReadOnlyList<Cipher> Ciphers,
    int SkippedCipherCount,
    int SkippedFolderCount,
    int UnsupportedOrganizationCipherCount = 0,
    int UnsupportedProviderOrganizationCount = 0)
{
    public string? WrappedAccountPrivateKey { get; init; }

    public IReadOnlyList<VaultOrganization> Organizations { get; init; } =
        Array.Empty<VaultOrganization>();

    public IReadOnlyList<VaultCollection> Collections { get; init; } =
        Array.Empty<VaultCollection>();

    public IReadOnlyList<VaultOrganizationPolicy> Policies { get; init; } =
        Array.Empty<VaultOrganizationPolicy>();

    public IReadOnlyList<VaultOrganizationPolicyContext>? PolicyOrganizations { get; init; }

    public IReadOnlyList<Send> Sends { get; init; } = Array.Empty<Send>();

    public int SkippedSendCount { get; init; }

    public VaultDomainSettings? Domains { get; init; }
}

public sealed class VaultDecryptor
{
    private readonly CryptoService _crypto;
    private readonly CipherBlobCodec _blob;
    private readonly SendDecryptor _sendDecryptor;
    private readonly V2AccountKeysValidator _v2AccountKeysValidator;

    public VaultDecryptor(
        CryptoService crypto,
        V2AccountKeysValidator? v2AccountKeysValidator = null)
    {
        _crypto = crypto;
        _blob = new CipherBlobCodec(crypto);
        _sendDecryptor = new SendDecryptor(new SendCryptoService(crypto));
        _v2AccountKeysValidator = v2AccountKeysValidator ?? new V2AccountKeysValidator();
    }

    public DecryptedVault Decrypt(SyncResponse sync, SymmetricCryptoKey userKey, string serverUrl)
        => Decrypt(sync, userKey, ServerEndpoints.FromWebVault(serverUrl));

    public DecryptedVault Decrypt(
        SyncResponse sync,
        SymmetricCryptoKey userKey,
        ServerEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var account = BuildAccount(sync.Profile, endpoints);
        var wrappedAccountPrivateKey = ResolveWrappedAccountPrivateKey(sync.Profile);
        var organizations = BuildOrganizations(sync.Profile);
        var organizationKeyResolution = DecryptOrganizationKeys(
            sync.Profile,
            wrappedAccountPrivateKey,
            userKey);
        var organizationKeys = organizationKeyResolution.Keys;
        try
        {
            var folders = new List<Folder>();
            var skippedFolders = 0;
            foreach (var folder in sync.Folders ?? Array.Empty<FolderDto>())
            {
                try
                {
                    folders.Add(DecryptFolder(folder, userKey));
                }
                catch (Exception ex) when (IsFolderDecryptException(ex))
                {
                    skippedFolders++;
                }
            }

            var ciphers = new List<Cipher>();
            var skippedCiphers = 0;
            var unsupportedOrganizationCiphers = 0;
            foreach (var cipher in sync.Ciphers ?? Array.Empty<CipherDto>())
            {
                var wrappingKey = userKey;
                if (!string.IsNullOrWhiteSpace(cipher.OrganizationId)
                    && !organizationKeys.TryGetValue(cipher.OrganizationId, out wrappingKey))
                {
                    unsupportedOrganizationCiphers++;
                    continue;
                }

                try
                {
                    ciphers.Add(DecryptCipher(cipher, wrappingKey));
                }
                catch (Exception ex) when (IsCipherDecryptException(ex))
                {
                    skippedCiphers++;
                }
            }

            var sendResolution = DecryptSends(sync.Sends, userKey, endpoints.SendWeb.Value);
            var policies = BuildEffectivePolicies(sync);
            var policyOrganizations = BuildPolicyOrganizations(sync.Profile);
            var visibleCiphers = ciphers
                .Where(cipher => !VaultPolicyEvaluator.IsCipherRestricted(
                    cipher,
                    policyOrganizations,
                    policies))
                .ToArray();

            return new DecryptedVault(
                account,
                folders,
                visibleCiphers,
                skippedCiphers,
                skippedFolders,
                unsupportedOrganizationCiphers,
                organizationKeyResolution.UnsupportedProviderOrganizationCount)
            {
                WrappedAccountPrivateKey = wrappedAccountPrivateKey,
                Organizations = organizations,
                Collections = DecryptCollections(sync.Collections, organizationKeys),
                Policies = policies,
                PolicyOrganizations = policyOrganizations,
                Sends = sendResolution.Sends,
                SkippedSendCount = sendResolution.SkippedCount,
                Domains = BuildDomains(sync.Domains),
            };
        }
        finally
        {
            foreach (var key in organizationKeys.Values)
                key.Dispose();
        }
    }

    public DecryptedVault Decrypt(
        SyncResponse sync,
        VaultUserKey userKey,
        ServerEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(userKey);
        return userKey.Legacy is { } legacy
            ? Decrypt(sync, legacy, endpoints)
            : DecryptV2(sync, userKey.V2!, endpoints);
    }

    public DecryptedVault Decrypt(
        SyncResponse sync,
        V2UserKey userKey,
        ServerEndpoints endpoints) => DecryptV2(sync, userKey, endpoints);

    private DecryptedVault DecryptV2(
        SyncResponse sync,
        V2UserKey userKey,
        ServerEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(userKey);
        ArgumentNullException.ThrowIfNull(endpoints);

        var account = BuildAccount(sync.Profile, endpoints);
        var wrappedAccountPrivateKey = ResolveV2WrappedAccountPrivateKey(sync.Profile, userKey);
        var organizations = BuildOrganizations(sync.Profile);
        var organizationKeyResolution = DecryptOrganizationKeysV2(
            sync.Profile,
            wrappedAccountPrivateKey,
            userKey);
        var organizationKeys = organizationKeyResolution.Keys;
        try
        {
            var folders = new List<Folder>();
            var skippedFolders = 0;
            foreach (var folder in sync.Folders ?? Array.Empty<FolderDto>())
            {
                try
                {
                    folders.Add(DecryptFolder(folder, userKey));
                }
                catch (Exception exception) when (IsFolderDecryptException(exception))
                {
                    skippedFolders++;
                }
            }

            var ciphers = new List<Cipher>();
            var skippedCiphers = 0;
            var unsupportedOrganizationCiphers = 0;
            foreach (var cipher in sync.Ciphers ?? Array.Empty<CipherDto>())
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(cipher.OrganizationId))
                    {
                        ciphers.Add(DecryptCipher(cipher, userKey));
                    }
                    else if (organizationKeys.TryGetValue(
                        cipher.OrganizationId,
                        out var organizationKey))
                    {
                        ciphers.Add(DecryptCipher(cipher, organizationKey));
                    }
                    else
                    {
                        unsupportedOrganizationCiphers++;
                    }
                }
                catch (Exception exception) when (IsCipherDecryptException(exception))
                {
                    skippedCiphers++;
                }
            }

            var sendResolution = DecryptSends(sync.Sends, userKey, endpoints.SendWeb.Value);
            var policies = BuildEffectivePolicies(sync);
            var policyOrganizations = BuildPolicyOrganizations(sync.Profile);
            var visibleCiphers = ciphers
                .Where(cipher => !VaultPolicyEvaluator.IsCipherRestricted(
                    cipher,
                    policyOrganizations,
                    policies))
                .ToArray();
            return new DecryptedVault(
                account,
                folders,
                visibleCiphers,
                skippedCiphers,
                skippedFolders,
                unsupportedOrganizationCiphers,
                organizationKeyResolution.UnsupportedProviderOrganizationCount)
            {
                WrappedAccountPrivateKey = wrappedAccountPrivateKey,
                Organizations = organizations,
                Collections = DecryptCollections(sync.Collections, organizationKeys),
                Policies = policies,
                PolicyOrganizations = policyOrganizations,
                Sends = sendResolution.Sends,
                SkippedSendCount = sendResolution.SkippedCount,
                Domains = BuildDomains(sync.Domains),
            };
        }
        finally
        {
            foreach (var key in organizationKeys.Values)
                key.Dispose();
        }
    }

    public Folder DecryptFolder(FolderDto folder, SymmetricCryptoKey userKey) => new()
    {
        Id = folder.Id,
        Name = Dec(folder.Name, userKey) ?? string.Empty,
        RevisionDate = folder.RevisionDate ?? DateTimeOffset.MinValue,
    };

    public Folder DecryptFolder(FolderDto folder, V2UserKey userKey)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(userKey);
        if (string.IsNullOrWhiteSpace(folder.Name))
            throw new InvalidDataException("Folder name is missing.");

        var plaintext = userKey.Decrypt(folder.Name, CoseContentFormat.Utf8);
        try
        {
            return new Folder
            {
                Id = folder.Id,
                Name = new System.Text.UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true).GetString(plaintext),
                RevisionDate = folder.RevisionDate ?? DateTimeOffset.MinValue,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private OrganizationKeyResolution DecryptOrganizationKeys(
        ProfileDto? profile,
        string? wrappedAccountPrivateKey,
        SymmetricCryptoKey userKey)
    {
        var result = new Dictionary<string, SymmetricCryptoKey>(StringComparer.Ordinal);
        var providerKeys = new Dictionary<string, SymmetricCryptoKey>(StringComparer.Ordinal);
        byte[]? privateKey = null;
        var resultTransferred = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(wrappedAccountPrivateKey))
            {
                try
                {
                    var privateKeyEnvelope = EncString.Parse(wrappedAccountPrivateKey);
                    if (privateKeyEnvelope.Type != EncryptionType.AesCbc256_HmacSha256_B64)
                    {
                        throw new CryptographicException(
                            "The account private key must use encryption type 2.");
                    }

                    privateKey = _crypto.Decrypt(privateKeyEnvelope, userKey);
                }
                catch (Exception ex) when (IsOrganizationKeyException(ex))
                {
                    privateKey = null;
                }
            }

            var ordinaryOrganizationGroups = (profile?.Organizations
                    ?? Array.Empty<ProfileOrganizationDto>())
                .GroupBy(organization => organization.Id ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var group in ordinaryOrganizationGroups)
            {
                if (string.IsNullOrWhiteSpace(group.Key) || group.Value.Length != 1)
                    continue;

                var organization = group.Value[0];
                if (!organization.Enabled || string.IsNullOrWhiteSpace(organization.Key))
                    continue;

                byte[]? keyBytes = null;
                try
                {
                    var encryptedKey = EncString.Parse(organization.Key);
                    if (privateKey is null)
                        continue;
                    keyBytes = OrganizationKeyResolver.DecryptV1DirectOrganizationKey(
                        _crypto,
                        encryptedKey,
                        privateKey);

                    var organizationKey = new SymmetricCryptoKey(keyBytes);
                    keyBytes = null;
                    if (!result.TryAdd(organization.Id, organizationKey))
                        organizationKey.Dispose();
                }
                catch (Exception ex) when (IsOrganizationKeyException(ex))
                {
                    // A single invalid membership key must not hide other organizations.
                }
                finally
                {
                    if (keyBytes is not null)
                        CryptographicOperations.ZeroMemory(keyBytes);
                }
            }

            var providerOrganizations = profile?.ProviderOrganizations
                ?? Array.Empty<ProfileProviderOrganizationDto>();
            var unsupportedProviderOrganizations = 0;
            if (providerOrganizations.Length == 0)
            {
                resultTransferred = true;
                return new OrganizationKeyResolution(result, 0);
            }

            var providerGroups = (profile?.Providers ?? Array.Empty<ProfileProviderDto>())
                .GroupBy(provider => provider.Id ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

            if (privateKey is not null)
            {
                foreach (var group in providerGroups)
                {
                    if (string.IsNullOrWhiteSpace(group.Key) || group.Value.Length != 1)
                        continue;

                    var provider = group.Value[0];
                    if (string.IsNullOrWhiteSpace(provider.Key))
                        continue;

                    byte[]? keyBytes = null;
                    try
                    {
                        var encryptedProviderKey = EncString.Parse(provider.Key);
                        if (!encryptedProviderKey.Type.IsRsaEnvelope())
                        {
                            continue;
                        }

                        keyBytes = _crypto.DecryptRsa(encryptedProviderKey, privateKey);
                        if (keyBytes.Length != 64)
                            continue;

                        var providerKey = new SymmetricCryptoKey(keyBytes);
                        keyBytes = null;
                        if (!providerKeys.TryAdd(group.Key, providerKey))
                            providerKey.Dispose();
                    }
                    catch (Exception ex) when (IsOrganizationKeyException(ex))
                    {
                        // Provider keys never fall back to the user key.
                    }
                    finally
                    {
                        if (keyBytes is not null)
                            CryptographicOperations.ZeroMemory(keyBytes);
                    }
                }
            }

            foreach (var group in providerOrganizations
                .GroupBy(organization => organization.Id ?? string.Empty, StringComparer.Ordinal))
            {
                var organizationId = group.Key;
                var entries = group.ToArray();
                RemoveKey(result, organizationId);
                if (string.IsNullOrWhiteSpace(organizationId)
                    || entries.Length != 1)
                {
                    unsupportedProviderOrganizations++;
                    continue;
                }

                var organization = entries[0];
                if (string.IsNullOrWhiteSpace(organization.ProviderId)
                    || string.IsNullOrWhiteSpace(organization.Key)
                    || !providerGroups.TryGetValue(organization.ProviderId, out var matchingProviders)
                    || matchingProviders.Length != 1
                    || !providerKeys.TryGetValue(organization.ProviderId, out var providerKey))
                {
                    unsupportedProviderOrganizations++;
                    continue;
                }

                byte[]? keyBytes = null;
                try
                {
                    var encryptedOrganizationKey = EncString.Parse(organization.Key);
                    if (encryptedOrganizationKey.Type != EncryptionType.AesCbc256_HmacSha256_B64)
                        throw new CryptographicException("Provider organization key must use encryption type 2.");

                    keyBytes = _crypto.Decrypt(encryptedOrganizationKey, providerKey);
                    if (keyBytes.Length is not (32 or 64))
                        throw new CryptographicException(
                            "Provider organization key must contain 32 or 64 bytes.");

                    var organizationKey = new SymmetricCryptoKey(keyBytes);
                    keyBytes = null;
                    if (!result.TryAdd(organizationId, organizationKey))
                    {
                        organizationKey.Dispose();
                        unsupportedProviderOrganizations++;
                    }
                }
                catch (Exception ex) when (IsOrganizationKeyException(ex))
                {
                    unsupportedProviderOrganizations++;
                }
                finally
                {
                    if (keyBytes is not null)
                        CryptographicOperations.ZeroMemory(keyBytes);
                }
            }

            resultTransferred = true;
            return new OrganizationKeyResolution(result, unsupportedProviderOrganizations);
        }
        finally
        {
            foreach (var providerKey in providerKeys.Values)
                providerKey.Dispose();
            if (privateKey is not null)
                CryptographicOperations.ZeroMemory(privateKey);
            if (!resultTransferred)
            {
                foreach (var organizationKey in result.Values)
                    organizationKey.Dispose();
            }
        }
    }

    private OrganizationKeyResolution DecryptOrganizationKeysV2(
        ProfileDto? profile,
        string wrappedAccountPrivateKey,
        V2UserKey userKey)
    {
        var result = new Dictionary<string, SymmetricCryptoKey>(StringComparer.Ordinal);
        var providerKeys = new Dictionary<string, SymmetricCryptoKey>(StringComparer.Ordinal);
        byte[]? privateKey = null;
        var resultTransferred = false;
        try
        {
            privateKey = userKey.Decrypt(
                wrappedAccountPrivateKey,
                CoseContentFormat.Pkcs8PrivateKey);

            foreach (var group in (profile?.Organizations
                    ?? Array.Empty<ProfileOrganizationDto>())
                .GroupBy(organization => organization.Id ?? string.Empty, StringComparer.Ordinal))
            {
                var entries = group.ToArray();
                if (string.IsNullOrWhiteSpace(group.Key)
                    || entries.Length != 1
                    || !entries[0].Enabled
                    || string.IsNullOrWhiteSpace(entries[0].Key))
                {
                    continue;
                }

                byte[]? keyBytes = null;
                try
                {
                    var encryptedKey = entries[0].Key!;
                    if (encryptedKey.StartsWith("7.", StringComparison.Ordinal))
                    {
                        keyBytes = userKey.Decrypt(
                            encryptedKey,
                            CoseContentFormat.BitwardenLegacyKey);
                    }
                    else
                    {
                        var rsaEnvelope = EncString.Parse(encryptedKey);
                        if (!rsaEnvelope.Type.IsRsaEnvelope())
                            throw new CryptographicException(
                                "A V2 organization key must use type 7 or an RSA envelope.");
                        keyBytes = _crypto.DecryptRsa(rsaEnvelope, privateKey);
                    }
                    AddOrganizationKey(result, entries[0].Id, keyBytes);
                    keyBytes = null;
                }
                catch (Exception exception) when (IsOrganizationKeyException(exception))
                {
                    // A single invalid membership key must not hide other organizations.
                }
                finally
                {
                    if (keyBytes is not null)
                        CryptographicOperations.ZeroMemory(keyBytes);
                }
            }

            var providerOrganizations = profile?.ProviderOrganizations
                ?? Array.Empty<ProfileProviderOrganizationDto>();
            if (providerOrganizations.Length == 0)
            {
                resultTransferred = true;
                return new OrganizationKeyResolution(result, 0);
            }

            var providerGroups = (profile?.Providers ?? Array.Empty<ProfileProviderDto>())
                .GroupBy(provider => provider.Id ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var group in providerGroups)
            {
                var entries = group.Value;
                if (string.IsNullOrWhiteSpace(group.Key)
                    || entries.Length != 1
                    || string.IsNullOrWhiteSpace(entries[0].Key))
                {
                    continue;
                }

                byte[]? keyBytes = null;
                try
                {
                    var rsaEnvelope = EncString.Parse(entries[0].Key!);
                    if (!rsaEnvelope.Type.IsRsaEnvelope())
                        continue;
                    keyBytes = _crypto.DecryptRsa(rsaEnvelope, privateKey);
                    if (keyBytes.Length != 64)
                        continue;
                    var providerKey = new SymmetricCryptoKey(keyBytes);
                    keyBytes = null;
                    if (!providerKeys.TryAdd(group.Key, providerKey))
                        providerKey.Dispose();
                }
                catch (Exception exception) when (IsOrganizationKeyException(exception))
                {
                    // Provider keys never fall back to the V2 user key.
                }
                finally
                {
                    if (keyBytes is not null)
                        CryptographicOperations.ZeroMemory(keyBytes);
                }
            }

            var unsupportedProviderOrganizations = 0;
            foreach (var group in providerOrganizations.GroupBy(
                organization => organization.Id ?? string.Empty,
                StringComparer.Ordinal))
            {
                var organizationId = group.Key;
                var entries = group.ToArray();
                RemoveKey(result, organizationId);
                if (string.IsNullOrWhiteSpace(organizationId) || entries.Length != 1)
                {
                    unsupportedProviderOrganizations++;
                    continue;
                }

                var organization = entries[0];
                if (string.IsNullOrWhiteSpace(organization.ProviderId)
                    || string.IsNullOrWhiteSpace(organization.Key)
                    || !providerGroups.TryGetValue(organization.ProviderId, out var matchingProviders)
                    || matchingProviders.Length != 1
                    || !providerKeys.TryGetValue(organization.ProviderId, out var providerKey))
                {
                    unsupportedProviderOrganizations++;
                    continue;
                }

                byte[]? keyBytes = null;
                try
                {
                    var encryptedOrganizationKey = EncString.Parse(organization.Key);
                    if (encryptedOrganizationKey.Type
                        != EncryptionType.AesCbc256_HmacSha256_B64)
                    {
                        throw new CryptographicException(
                            "Provider organization key must use encryption type 2.");
                    }
                    keyBytes = _crypto.Decrypt(encryptedOrganizationKey, providerKey);
                    AddOrganizationKey(result, organizationId, keyBytes);
                    keyBytes = null;
                }
                catch (Exception exception) when (IsOrganizationKeyException(exception))
                {
                    unsupportedProviderOrganizations++;
                }
                finally
                {
                    if (keyBytes is not null)
                        CryptographicOperations.ZeroMemory(keyBytes);
                }
            }

            resultTransferred = true;
            return new OrganizationKeyResolution(result, unsupportedProviderOrganizations);
        }
        finally
        {
            foreach (var providerKey in providerKeys.Values)
                providerKey.Dispose();
            if (privateKey is not null)
                CryptographicOperations.ZeroMemory(privateKey);
            if (!resultTransferred)
            {
                foreach (var organizationKey in result.Values)
                    organizationKey.Dispose();
            }
        }
    }

    private static void AddOrganizationKey(
        Dictionary<string, SymmetricCryptoKey> result,
        string organizationId,
        byte[] keyBytes)
    {
        if (keyBytes.Length is not (32 or 64))
            throw new CryptographicException(
                "Organization key must contain 32 or 64 bytes.");
        var organizationKey = new SymmetricCryptoKey(keyBytes);
        if (!result.TryAdd(organizationId, organizationKey))
            organizationKey.Dispose();
    }

    private IReadOnlyList<VaultCollection> DecryptCollections(
        CollectionDto[]? collections,
        IReadOnlyDictionary<string, SymmetricCryptoKey> organizationKeys)
    {
        var result = new List<VaultCollection>();
        foreach (var collection in collections ?? Array.Empty<CollectionDto>())
        {
            if (!organizationKeys.TryGetValue(collection.OrganizationId, out var organizationKey))
                continue;

            try
            {
                result.Add(new VaultCollection(
                    collection.Id,
                    collection.OrganizationId,
                    Dec(collection.Name, organizationKey) ?? string.Empty,
                    collection.ReadOnly,
                    collection.HidePasswords,
                    collection.Manage,
                    collection.ExternalId));
            }
            catch (Exception ex) when (IsCipherDecryptException(ex))
            {
                // Keep processing collections from the same organization.
            }
        }

        return result;
    }

    private static IReadOnlyList<VaultOrganization> BuildOrganizations(ProfileDto? profile)
    {
        var organizationsById = new Dictionary<string, VaultOrganization>(StringComparer.Ordinal);
        var organizationOrder = new List<string>();

        foreach (var group in (profile?.Organizations ?? Array.Empty<ProfileOrganizationDto>())
            .GroupBy(organization => organization.Id ?? string.Empty, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
                continue;

            var entries = group.ToArray();
            var organization = entries[0];
            AddOrReplaceOrganization(
                organizationsById,
                organizationOrder,
                new VaultOrganization(
                    organization.Id,
                    organization.Name ?? string.Empty,
                    organization.Status,
                    organization.Type,
                    organization.Enabled)
                {
                    EncryptedKey = entries.Length == 1 ? organization.Key : null,
                    ProviderId = organization.ProviderId,
                    IsMember = true,
                    IsProviderUser = false,
                    ProductTierType = organization.ProductTierType,
                });
        }

        var providersById = (profile?.Providers ?? Array.Empty<ProfileProviderDto>())
            .GroupBy(provider => provider.Id ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var group in (profile?.ProviderOrganizations
                ?? Array.Empty<ProfileProviderOrganizationDto>())
            .GroupBy(organization => organization.Id ?? string.Empty, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
                continue;

            var entries = group.ToArray();
            var organization = entries[0];
            string? encryptedProviderKey = null;
            if (entries.Length == 1
                && !string.IsNullOrWhiteSpace(organization.ProviderId)
                && providersById.TryGetValue(organization.ProviderId, out var providers)
                && providers.Length == 1)
            {
                encryptedProviderKey = providers[0].Key;
            }

            var metadata = organizationsById.TryGetValue(group.Key, out var ordinaryOrganization)
                ? ordinaryOrganization
                : new VaultOrganization(
                    group.Key,
                    organization.Name ?? string.Empty,
                    organization.Status,
                    organization.Type,
                    organization.Enabled)
                {
                    IsMember = false,
                    IsProviderUser = true,
                    ProductTierType = null,
                };
            AddOrReplaceOrganization(
                organizationsById,
                organizationOrder,
                new VaultOrganization(
                    group.Key,
                    metadata.Name,
                    metadata.Status,
                    metadata.Type,
                    metadata.Enabled)
                {
                    EncryptedKey = entries.Length == 1 ? organization.Key : null,
                    ProviderId = organization.ProviderId,
                    EncryptedProviderKey = encryptedProviderKey,
                    IsMember = metadata.IsMember,
                    IsProviderUser = true,
                    IsProviderOrganization = true,
                    ProductTierType = metadata.ProductTierType,
                });
        }

        return organizationOrder.Select(id => organizationsById[id]).ToArray();
    }

    private static void AddOrReplaceOrganization(
        Dictionary<string, VaultOrganization> organizationsById,
        List<string> organizationOrder,
        VaultOrganization organization)
    {
        if (!organizationsById.ContainsKey(organization.Id))
            organizationOrder.Add(organization.Id);
        organizationsById[organization.Id] = organization;
    }

    private string ResolveV2WrappedAccountPrivateKey(
        ProfileDto? profile,
        V2UserKey userKey)
    {
        if (profile?.AccountKeys is not JsonElement accountKeys
            || accountKeys.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "A V2 sync profile is missing its authenticated account keys.");
        }

        var parsed = accountKeys.Deserialize(ApiJsonContext.Default.AccountKeysResponse)
            ?? throw new InvalidDataException("V2 account keys could not be decoded.");
        return _v2AccountKeysValidator.Validate(parsed, userKey).WrappedPrivateKey;
    }

    private static string? ResolveWrappedAccountPrivateKey(ProfileDto? profile)
    {
        if (profile?.AccountKeys is not JsonElement accountKeys
            || accountKeys.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            var legacyPrivateKey = profile?.PrivateKey;
            return IsType2EncString(legacyPrivateKey) ? legacyPrivateKey : null;
        }

        return TryGetSupportedV1AccountPrivateKey(accountKeys, out var wrappedPrivateKey)
            ? wrappedPrivateKey
            : null;
    }

    private static bool IsType2EncString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            return EncString.Parse(value).Type == EncryptionType.AesCbc256_HmacSha256_B64;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryGetSupportedV1AccountPrivateKey(
        JsonElement accountKeys,
        out string? wrappedPrivateKey)
    {
        wrappedPrivateKey = null;
        if (accountKeys.ValueKind != JsonValueKind.Object)
            return false;

        if (!IsAbsentOrNullUniqueProperty(accountKeys, "signatureKeyPair")
            || !IsAbsentOrNullUniqueProperty(accountKeys, "securityState")
            || !TryGetUniqueProperty(accountKeys, "publicKeyEncryptionKeyPair", out var foundKeyPair, out var keyPair)
            || !foundKeyPair
            || keyPair.ValueKind != JsonValueKind.Object
            || !TryGetUniqueStringProperty(keyPair, "publicKey", out _)
            || !TryGetUniqueStringProperty(keyPair, "wrappedPrivateKey", out var candidatePrivateKey)
            || !IsAbsentOrNullUniqueProperty(keyPair, "signedPublicKey"))
        {
            return false;
        }

        try
        {
            if (EncString.Parse(candidatePrivateKey).Type
                != EncryptionType.AesCbc256_HmacSha256_B64)
            {
                return false;
            }

            wrappedPrivateKey = candidatePrivateKey;
            return true;
        }
        catch (Exception ex) when (IsOrganizationKeyException(ex))
        {
            return false;
        }
    }

    private static bool IsAbsentOrNullUniqueProperty(
        JsonElement value,
        string propertyName) =>
        TryGetUniqueProperty(value, propertyName, out var found, out var property)
        && (!found || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);

    private static bool TryGetUniqueProperty(
        JsonElement value,
        string propertyName,
        out bool found,
        out JsonElement property)
    {
        found = false;
        property = default;
        foreach (var candidate in value.EnumerateObject())
        {
            if (!string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (found)
                return false;

            found = true;
            property = candidate.Value;
        }

        return true;
    }

    private static bool TryGetUniqueStringProperty(
        JsonElement value,
        string propertyName,
        out string property)
    {
        property = string.Empty;
        if (!TryGetUniqueProperty(value, propertyName, out var found, out var element)
            || !found
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        property = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(property);
    }

    private static void RemoveKey(
        Dictionary<string, SymmetricCryptoKey> keys,
        string organizationId)
    {
        if (!string.IsNullOrWhiteSpace(organizationId)
            && keys.Remove(organizationId, out var key))
        {
            key.Dispose();
        }
    }

    private static IReadOnlyList<VaultOrganizationPolicy> BuildEffectivePolicies(
        SyncResponse sync)
    {
        var source = sync.PoliciesNew is { Length: > 0 }
            ? sync.PoliciesNew
            : sync.Policies;
        var result = new List<VaultOrganizationPolicy>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in source ?? Array.Empty<OrganizationPolicyDto>())
        {
            if (policy is null
                || string.IsNullOrWhiteSpace(policy.Id)
                || string.IsNullOrWhiteSpace(policy.OrganizationId)
                || !ids.Add(policy.Id))
            {
                throw new InvalidDataException("Sync contains an invalid organization policy.");
            }

            result.Add(new VaultOrganizationPolicy(
                policy.Id,
                policy.OrganizationId,
                policy.Type,
                policy.Enabled,
                policy.Data is not JsonElement data
                    || data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? "null"
                    : data.GetRawText())
            {
                RevisionDate = policy.RevisionDate,
                CanToggleState = policy.CanToggleState ?? true,
            });
        }

        return result;
    }

    private static IReadOnlyList<VaultOrganizationPolicyContext> BuildPolicyOrganizations(
        ProfileDto? profile)
    {
        var result = new Dictionary<string, VaultOrganizationPolicyContext>(
            StringComparer.OrdinalIgnoreCase);
        var source = profile?.OrganizationsNew
            ?? profile?.Organizations
            ?? Array.Empty<ProfileOrganizationDto>();

        foreach (var group in source
            .Where(organization => organization is not null)
            .GroupBy(organization => organization.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var entries = group.ToArray();
            if (string.IsNullOrWhiteSpace(group.Key) || entries.Length != 1)
                continue;
            var organization = entries[0];
            result[group.Key] = new VaultOrganizationPolicyContext(
                organization.Id,
                organization.Status,
                organization.Type,
                organization.Enabled,
                organization.UsePolicies,
                IsProviderUser: false);
        }

        foreach (var group in (profile?.ProviderOrganizations
                ?? Array.Empty<ProfileProviderOrganizationDto>())
            .Where(organization => organization is not null)
            .GroupBy(organization => organization.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var entries = group.ToArray();
            if (string.IsNullOrWhiteSpace(group.Key) || entries.Length != 1)
                continue;
            var organization = entries[0];
            if (result.TryGetValue(group.Key, out var membership))
            {
                result[group.Key] = membership with { IsProviderUser = true };
            }
            else
            {
                result[group.Key] = new VaultOrganizationPolicyContext(
                    group.Key,
                    organization.Status,
                    organization.Type,
                    organization.Enabled,
                    UsePolicies: true,
                    IsProviderUser: true);
            }
        }

        return result.Values.ToArray();
    }

    private static VaultDomainSettings? BuildDomains(DomainsDto? domains)
    {
        if (domains is null)
            return null;

        var equivalents = (domains.EquivalentDomains ?? Array.Empty<string[]>())
            .Where(group => group is not null)
            .Select(group => (IReadOnlyList<string>)group
                .Where(domain => !string.IsNullOrWhiteSpace(domain))
                .ToArray())
            .ToArray();
        var globals = (domains.GlobalEquivalentDomains
                ?? Array.Empty<GlobalEquivalentDomainDto>())
            .Where(global => global is not null)
            .Select(global => new VaultGlobalEquivalentDomain(
                global.Type,
                (global.Domains ?? Array.Empty<string>())
                    .Where(domain => !string.IsNullOrWhiteSpace(domain))
                    .ToArray(),
                global.Excluded))
            .ToArray();
        return new VaultDomainSettings(equivalents, globals);
    }

    private SendResolution DecryptSends(
        SendResponseDto[]? sends,
        SymmetricCryptoKey userKey,
        string sendWebUrl) =>
        DecryptSendsCore(
            sends,
            (send, witness) => _sendDecryptor.Decrypt(
                send,
                userKey,
                sendWebUrl,
                witness));

    private SendResolution DecryptSends(
        SendResponseDto[]? sends,
        V2UserKey userKey,
        string sendWebUrl) =>
        DecryptSendsCore(
            sends,
            (send, witness) => _sendDecryptor.Decrypt(
                send,
                userKey,
                sendWebUrl,
                witness));

    private static SendResolution DecryptSendsCore(
        SendResponseDto[]? sends,
        Func<SendResponseDto, string, Send> decrypt)
    {
        var result = new List<Send>();
        var skipped = 0;
        foreach (var group in (sends ?? Array.Empty<SendResponseDto>())
            .Where(send => send is not null)
            .GroupBy(send => send.Id ?? string.Empty, StringComparer.Ordinal))
        {
            var entries = group.ToArray();
            if (string.IsNullOrWhiteSpace(group.Key) || entries.Length != 1)
            {
                skipped += entries.Length;
                continue;
            }

            try
            {
                var send = entries[0];
                result.Add(decrypt(send, SendMutationWitness.Compute(send)));
            }
            catch (Exception exception) when (IsSendDecryptException(exception))
            {
                skipped++;
            }
        }

        skipped += (sends ?? Array.Empty<SendResponseDto>()).Count(send => send is null);
        return new SendResolution(result, skipped);
    }

    private static bool IsSendDecryptException(Exception exception) =>
        exception is CryptographicException
            or FormatException
            or ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or NullReferenceException
            or OverflowException;

    public Cipher DecryptCipher(CipherDto dto, SymmetricCryptoKey userKey)
    {
        using var ownedItemKey = string.IsNullOrWhiteSpace(dto.Key)
            ? null
            : _crypto.DecryptItemKey(dto.Key, userKey);
        var key = ownedItemKey ?? userKey;
        return _blob.IsBlob(dto.Data)
            ? DecryptBlobCipher(dto, key)
            : DecryptLegacyCipher(dto, key);
    }

    public Cipher DecryptCipher(CipherDto dto, V2UserKey userKey)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(userKey);
        if (string.IsNullOrWhiteSpace(dto.Key))
        {
            throw new CryptographicException(
                "A V2 personal cipher without an item key cannot be safely decrypted.");
        }

        var itemKeyBytes = userKey.Decrypt(
            dto.Key,
            CoseContentFormat.BitwardenLegacyKey);
        try
        {
            if (itemKeyBytes.Length != 64)
                throw new CryptographicException(
                    "A V2 cipher key must contain a 64-byte AES-CBC-HMAC key.");
            using var itemKey = new SymmetricCryptoKey(itemKeyBytes);
            itemKeyBytes = Array.Empty<byte>();
            return _blob.IsBlob(dto.Data)
                ? DecryptBlobCipher(dto, itemKey)
                : DecryptLegacyCipher(dto, itemKey);
        }
        finally
        {
            if (itemKeyBytes.Length > 0)
                CryptographicOperations.ZeroMemory(itemKeyBytes);
        }
    }

    private Cipher DecryptLegacyCipher(CipherDto dto, SymmetricCryptoKey key)
    {
        var type = (CipherType)dto.Type;
        var personal = string.IsNullOrWhiteSpace(dto.OrganizationId);
        var canEdit = dto.Edit ?? personal;

        return new Cipher
        {
            Id = dto.Id,
            Type = type,
            OrganizationId = dto.OrganizationId,
            CollectionIds = dto.CollectionIds ?? Array.Empty<string>(),
            // 原样携带服务端下发的包裹条目密钥,供编辑时复用(不轮换),避免损坏既有附件。
            Key = dto.Key,
            FolderId = dto.FolderId,
            Favorite = dto.Favorite,
            Reprompt = dto.Reprompt == 1,
            Name = Dec(dto.Name, key) ?? string.Empty,
            Notes = Dec(dto.Notes, key),
            CreationDate = dto.CreationDate ?? DateTimeOffset.MinValue,
            RevisionDate = dto.RevisionDate ?? DateTimeOffset.MinValue,
            DeletedDate = dto.DeletedDate,
            ArchivedDate = dto.ArchivedDate,
            CanEdit = canEdit,
            CanViewPassword = dto.ViewPassword ?? personal,
            CanDelete = dto.Permissions?.Delete ?? canEdit,
            CanRestore = dto.Permissions?.Restore ?? canEdit,
            Login = dto.Login is null ? null : DecryptLogin(dto.Login, key),
            Card = dto.Card is null ? null : DecryptCard(dto.Card, key),
            Identity = dto.Identity is null ? null : DecryptIdentity(dto.Identity, key),
            SecureNote = dto.SecureNote is null ? null : new CipherSecureNote(dto.SecureNote.Type),
            Ssh = dto.SshKey is null ? null : DecryptSsh(dto.SshKey, key),
            BankAccount = dto.BankAccount is null ? null : DecryptBankAccount(dto.BankAccount, key),
            DriversLicense = dto.DriversLicense is null ? null : DecryptDriversLicense(dto.DriversLicense, key),
            Passport = dto.Passport is null ? null : DecryptPassport(dto.Passport, key),
            Fields = DecryptFields(dto.Fields, key),
            Attachments = DecryptAttachments(dto.Attachments, key),
            PasswordHistory = DecryptPasswordHistory(dto.PasswordHistory, key),
        };
    }

    private Cipher DecryptBlobCipher(CipherDto dto, SymmetricCryptoKey itemKey)
    {
        if (string.IsNullOrWhiteSpace(dto.Data))
            throw new InvalidDataException("Blob-encrypted cipher is missing Cipher.data.");
        if (dto.Name is not null
            || dto.Notes is not null
            || dto.Login is not null
            || dto.Card is not null
            || dto.Identity is not null
            || dto.SecureNote is not null
            || dto.SshKey is not null
            || dto.BankAccount is not null
            || dto.DriversLicense is not null
            || dto.Passport is not null
            || dto.Fields is not null
            || dto.PasswordHistory is not null)
        {
            throw new InvalidDataException(
                "Blob-encrypted cipher also contains obsolete sensitive fields.");
        }

        var content = _blob.Decrypt(dto.Data, itemKey);
        if ((int)content.Type != dto.Type)
            throw new InvalidDataException(
                "Cipher metadata type does not match its encrypted blob payload.");
        var personal = string.IsNullOrWhiteSpace(dto.OrganizationId);
        var canEdit = dto.Edit ?? personal;
        return new Cipher
        {
            Id = dto.Id,
            Type = content.Type,
            OrganizationId = dto.OrganizationId,
            CollectionIds = dto.CollectionIds ?? Array.Empty<string>(),
            Key = dto.Key,
            UsesBlobEncryption = true,
            FolderId = dto.FolderId,
            Favorite = dto.Favorite,
            Reprompt = dto.Reprompt == 1,
            Name = content.Name,
            Notes = content.Notes,
            CreationDate = dto.CreationDate ?? DateTimeOffset.MinValue,
            RevisionDate = dto.RevisionDate ?? DateTimeOffset.MinValue,
            DeletedDate = dto.DeletedDate,
            ArchivedDate = dto.ArchivedDate,
            CanEdit = canEdit,
            CanViewPassword = dto.ViewPassword ?? personal,
            CanDelete = dto.Permissions?.Delete ?? canEdit,
            CanRestore = dto.Permissions?.Restore ?? canEdit,
            Login = content.Login,
            Card = content.Card,
            Identity = content.Identity,
            SecureNote = content.SecureNote,
            Ssh = content.Ssh,
            BankAccount = content.BankAccount,
            DriversLicense = content.DriversLicense,
            Passport = content.Passport,
            Fields = content.Fields,
            Attachments = DecryptAttachments(dto.Attachments, itemKey),
            PasswordHistory = content.PasswordHistory,
        };
    }

    // 解析条目有效密钥:dto.Key 为空则用 userKey,否则解出条目级密钥。
    public SymmetricCryptoKey ResolveItemKey(CipherDto dto, SymmetricCryptoKey userKey) =>
        string.IsNullOrWhiteSpace(dto.Key)
            ? userKey
            : _crypto.DecryptItemKey(dto.Key, userKey);

    public SymmetricCryptoKey ResolveItemKey(CipherDto dto, V2UserKey userKey)
    {
        if (string.IsNullOrWhiteSpace(dto.Key))
            throw new CryptographicException("A V2 personal cipher is missing its item key.");
        var bytes = userKey.Decrypt(dto.Key, CoseContentFormat.BitwardenLegacyKey);
        try
        {
            if (bytes.Length != 64)
                throw new CryptographicException(
                    "A V2 cipher key must contain a 64-byte AES-CBC-HMAC key.");
            var result = new SymmetricCryptoKey(bytes);
            bytes = Array.Empty<byte>();
            return result;
        }
        finally
        {
            if (bytes.Length > 0)
                CryptographicOperations.ZeroMemory(bytes);
        }
    }

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
                using var ownedAttachmentKey = string.IsNullOrEmpty(d.Key)
                    ? null
                    : _crypto.DecryptItemKey(d.Key, itemKey);
                var attachmentKey = ownedAttachmentKey ?? itemKey;
                var fileName = _crypto.DecryptToString(d.FileName, attachmentKey) ?? string.Empty;
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

    // 解密密码历史条目。密码为空或解密失败(坏 EncString)时跳过该条,不连累其余历史或整条目。
    private IReadOnlyList<PasswordHistoryEntry> DecryptPasswordHistory(PasswordHistoryDto[]? dtos, SymmetricCryptoKey key)
    {
        if (dtos is null || dtos.Length == 0)
            return Array.Empty<PasswordHistoryEntry>();
        var result = new List<PasswordHistoryEntry>(dtos.Length);
        foreach (var d in dtos)
        {
            if (string.IsNullOrEmpty(d.Password)) continue;
            string? pw;
            try { pw = Dec(d.Password, key); }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { continue; }
            if (string.IsNullOrEmpty(pw)) continue;
            DateTimeOffset? when = DateTimeOffset.TryParse(d.LastUsedDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;
            result.Add(new PasswordHistoryEntry(pw, when));
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
            PasswordRevisionDate = login.PasswordRevisionDate,
            AutofillOnPageLoad = login.AutofillOnPageLoad,
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

    private CipherBankAccount DecryptBankAccount(BankAccountDto account, SymmetricCryptoKey key) => new(
        Dec(account.BankName, key),
        Dec(account.NameOnAccount, key),
        Dec(account.AccountType, key),
        Dec(account.AccountNumber, key),
        Dec(account.RoutingNumber, key),
        Dec(account.BranchNumber, key),
        Dec(account.Pin, key),
        Dec(account.SwiftCode, key),
        Dec(account.Iban, key),
        Dec(account.BankContactPhone, key));

    private CipherDriversLicense DecryptDriversLicense(DriversLicenseDto license, SymmetricCryptoKey key) => new(
        Dec(license.FirstName, key),
        Dec(license.MiddleName, key),
        Dec(license.LastName, key),
        Dec(license.DateOfBirth, key),
        Dec(license.LicenseNumber, key),
        Dec(license.IssuingCountry, key),
        Dec(license.IssuingState, key),
        Dec(license.IssueDate, key),
        Dec(license.ExpirationDate, key),
        Dec(license.IssuingAuthority, key),
        Dec(license.LicenseClass, key));

    private CipherPassport DecryptPassport(PassportDto passport, SymmetricCryptoKey key) => new(
        Dec(passport.Surname, key),
        Dec(passport.GivenName, key),
        Dec(passport.DateOfBirth, key),
        Dec(passport.Sex, key),
        Dec(passport.BirthPlace, key),
        Dec(passport.Nationality, key),
        Dec(passport.IssuingCountry, key),
        Dec(passport.PassportNumber, key),
        Dec(passport.PassportType, key),
        Dec(passport.NationalIdentificationNumber, key),
        Dec(passport.IssuingAuthority, key),
        Dec(passport.IssueDate, key),
        Dec(passport.ExpirationDate, key));

    private IReadOnlyList<CipherField> DecryptFields(FieldDto[]? fields, SymmetricCryptoKey key) =>
        (fields ?? Array.Empty<FieldDto>())
            .Select(field => CipherField.FromWire(
                Dec(field.Name, key) ?? string.Empty,
                Dec(field.Value, key),
                field.Type,
                field.LinkedId))
            .ToArray();

    private static long? TryParseCounter(string? value) =>
        long.TryParse(value, out var counter) ? counter : null;

    private string? Dec(string? value, SymmetricCryptoKey key) => _crypto.DecryptToString(value, key);

    private static AccountInfo BuildAccount(ProfileDto? profile, ServerEndpoints endpoints)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.Id))
            throw new InvalidDataException("Sync profile is missing a valid account ID.");

        var email = profile.Email ?? string.Empty;
        var display = profile.Name ?? email;
        var initial = string.IsNullOrWhiteSpace(display)
            ? string.Empty
            : display.Trim()[0].ToString().ToUpperInvariant();
        return new AccountInfo(profile.Id, email, endpoints, initial, string.Empty)
        {
            Name = profile.Name ?? string.Empty,
            UserIsClaimedByOrganization = ResolveUserIsClaimedByOrganization(profile),
        };
    }

    private static bool? ResolveUserIsClaimedByOrganization(ProfileDto profile)
    {
        var claims = (profile.Organizations ?? Array.Empty<ProfileOrganizationDto>())
            .Concat(profile.OrganizationsNew ?? Array.Empty<ProfileOrganizationDto>())
            .Where(organization => organization is not null)
            .Select(organization => organization.UserIsClaimedByOrganization)
            .ToArray();
        if (claims.Any(claimed => claimed == true))
            return true;
        return claims.Length > 0 && claims.All(claimed => claimed == false)
            ? false
            : null;
    }

    private static bool IsCipherDecryptException(Exception ex) =>
        ex is CryptographicException
            or FormatException
            or System.Formats.Cbor.CborContentException
            or ArgumentException
            or InvalidOperationException
            or InvalidDataException
            or JsonException
            or OverflowException;

    private static bool IsOrganizationKeyException(Exception ex) =>
        ex is CryptographicException
            or FormatException
            or System.Formats.Cbor.CborContentException
            or ArgumentException
            or InvalidOperationException
            or InvalidDataException
            or JsonException
            or OverflowException;

    private static bool IsFolderDecryptException(Exception ex) =>
        ex is CryptographicException
            or FormatException
            or System.Formats.Cbor.CborContentException
            or ArgumentException
            or InvalidDataException
            or System.Text.DecoderFallbackException;

    private sealed record OrganizationKeyResolution(
        IReadOnlyDictionary<string, SymmetricCryptoKey> Keys,
        int UnsupportedProviderOrganizationCount);

    private sealed record SendResolution(
        IReadOnlyList<Send> Sends,
        int SkippedCount);
}
