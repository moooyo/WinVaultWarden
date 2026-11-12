using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Crypto.PasswordStrength;

namespace Vault;

/// <summary>
/// Coordinates account profile, password and KDF changes.
/// The application lifecycle coordinator handles logout after confirmed credential changes.
/// Requires an unlocked vault session with an active V1 or V2 user key.
/// </summary>
public sealed class AccountService : IAccountService
{
    private const int MaxNameLength = 50;
    private const int MaximumPasswordHintLength = 50;
    private static readonly Lazy<MasterPasswordPolicyEvaluator> DefaultPasswordPolicyEvaluator =
        new(() => new MasterPasswordPolicyEvaluator(
            new PasswordStrengthEvaluator(
                new Omnimatch(new DictionaryMatcher(FrequencyDictionaries.Load())))));

    private readonly CryptoService _crypto;
    private readonly IVaultApiClientProvider _clients;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;
    private readonly IAuthService _auth;
    private readonly VaultCacheService _cache;
    private readonly MasterPasswordPolicyEvaluator _passwordPolicyEvaluator;

    public AccountService(
        CryptoService crypto,
        IVaultApiClientProvider clients,
        VaultSession session,
        ITokenStore tokenStore,
        IAuthService auth,
        VaultCacheService cache,
        MasterPasswordPolicyEvaluator? passwordPolicyEvaluator = null)
    {
        _crypto = crypto;
        _clients = clients;
        _session = session;
        _tokenStore = tokenStore;
        _auth = auth;
        _cache = cache;
        _passwordPolicyEvaluator = passwordPolicyEvaluator ?? DefaultPasswordPolicyEvaluator.Value;
    }

    // -----------------------------------------------------------------------
    // UpdateNameAsync: normalize and validate, then update the profile without logout.
    // -----------------------------------------------------------------------

    public async Task UpdateNameAsync(string name, CancellationToken ct = default)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            throw new AccountOperationException("名字不能为空。");

        if (trimmed.Length > MaxNameLength)
            throw new AccountOperationException($"名字不能超过 {MaxNameLength} 个字符（当前 {trimmed.Length} 个）。");

        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IAccountApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.UpdateProfileAsync(new ProfileUpdateRequest(trimmed, "en-US"), linked.Token);
        _session.ThrowIfExpired(lease);
    }

    // -----------------------------------------------------------------------
    // ChangePasswordAsync:
    //   1. Verify the current password against the active account key.
    //   2. Derive a replacement master key and rewrap the unchanged account key.
    //   3. Submit authentication and unlock data to /api/accounts/password.
    //   4. Let the application lifecycle coordinator perform logout.
    // -----------------------------------------------------------------------

    public async Task ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        string? hint,
        CancellationToken ct = default)
    {
        var normalizedHint = ValidateNewPassword(currentPassword, newPassword, hint);
        ct.ThrowIfCancellationRequested();
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        if (!_session.TryCopyUserKeyMaterial(lease, out var copiedKey) || copiedKey is null)
            throw new AccountOperationException("操作需要已解锁的 Vault，请先解锁后重试。");
        using var userKey = copiedKey;

        if (!_tokenStore.TryLoad(out var persisted)
            || !_session.IsCurrent(lease, persisted))
            throw new AccountOperationException("持久化会话与当前账户不匹配。");

        var evidence = _passwordPolicyEvaluator.CaptureEvidence(newPassword);
        ValidateOrganizationPasswordPolicy(lease, evidence);

        // Verification owns and clears every temporary key, including failed attempts.
        byte[] oldMasterKey = await DeriveMasterKeyAsync(
            currentPassword,
            persisted.EffectiveMasterPasswordSalt,
            persisted.KdfType,
            persisted.KdfIterations,
            persisted.KdfMemory,
            persisted.KdfParallelism,
            linked.Token).ConfigureAwait(false);

        string oldHash;
        try
        {
            ValidateCurrentPassword(oldMasterKey, persisted, userKey);
            oldHash = _crypto.ComputeMasterPasswordHash(oldMasterKey, currentPassword);
        }
        catch (Exception ex) when (IsInvalidWrappedKey(ex))
        {
            throw new AccountOperationException("当前密码不正确。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldMasterKey);
        }

        // Password changes preserve the existing KDF parameters and salt.
        byte[] newMasterKey = await DeriveMasterKeyAsync(
            newPassword,
            persisted.EffectiveMasterPasswordSalt,
            persisted.KdfType,
            persisted.KdfIterations,
            persisted.KdfMemory,
            persisted.KdfParallelism,
            linked.Token).ConfigureAwait(false);

        string newHash;
        string wrappedKey;
        try
        {
            newHash = _crypto.ComputeMasterPasswordHash(newMasterKey, newPassword);
            wrappedKey = ProtectUserKey(newMasterKey, userKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newMasterKey);
        }

        _session.ThrowIfExpired(lease);
        var api = _clients.GetAuthenticated<IAccountApiClient>(lease);
        _session.ThrowIfExpired(lease);
        ValidateOrganizationPasswordPolicy(lease, evidence);
        var kdf = new KdfParams(
            Kdf: (int)persisted.KdfType,
            KdfIterations: persisted.KdfIterations,
            KdfMemory: persisted.KdfMemory,
            KdfParallelism: persisted.KdfParallelism);
        await api.ChangePasswordAsync(new ChangePasswordRequest(
            MasterPasswordHash: oldHash,
            NewMasterPasswordHash: newHash,
            MasterPasswordHint: normalizedHint,
            Key: wrappedKey,
            AuthenticationData: new AuthData(
                Salt: persisted.EffectiveMasterPasswordSalt,
                Kdf: kdf,
                MasterPasswordAuthenticationHash: newHash),
            UnlockData: new UnlockData(
                Salt: persisted.EffectiveMasterPasswordSalt,
                Kdf: kdf,
                MasterKeyWrappedUserKey: wrappedKey,
                ContainedKeyId: userKey.KeyId)), linked.Token).ConfigureAwait(false);
        _session.ThrowIfExpired(lease);

        // CredentialChanged owns the eventual token/cache cleanup and navigation.
    }

    // -----------------------------------------------------------------------
    // ChangeKdfAsync:
    //   1. Validate every selected algorithm parameter before allocating KDF work.
    //   2. Verify the current password against the active account key.
    //   3. Derive the new master key and rewrap the same V1 or V2 account key.
    //   4. Submit matching authentication/unlock KDFs to /api/accounts/kdf.
    // -----------------------------------------------------------------------

    public Task ChangeKdfAsync(
        string currentPassword,
        int newIterations,
        CancellationToken ct = default) =>
        ChangeKdfAsync(currentPassword, AccountKdfParameters.Pbkdf2(newIterations), ct);

    public async Task ChangeKdfAsync(
        string currentPassword,
        AccountKdfParameters parameters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!parameters.IsWriteCompatible)
            throw new AccountOperationException("The requested KDF parameters are outside the supported write range.");
        if (string.IsNullOrEmpty(currentPassword))
            throw new AccountOperationException("The current master password is required.");
        ct.ThrowIfCancellationRequested();

        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        if (!_session.TryCopyUserKeyMaterial(lease, out var copiedKey) || copiedKey is null)
            throw new AccountOperationException("操作需要已解锁的 Vault，请先解锁后重试。");
        using var userKey = copiedKey;

        if (!_tokenStore.TryLoad(out var persisted)
            || !_session.IsCurrent(lease, persisted))
            throw new AccountOperationException("持久化会话与当前账户不匹配。");

        // Verify the old credential before preparing replacement authentication data.
        byte[] oldMasterKey = await DeriveMasterKeyAsync(
            currentPassword,
            persisted.EffectiveMasterPasswordSalt,
            persisted.KdfType,
            persisted.KdfIterations,
            persisted.KdfMemory,
            persisted.KdfParallelism,
            linked.Token).ConfigureAwait(false);

        string oldHash;
        try
        {
            ValidateCurrentPassword(oldMasterKey, persisted, userKey);
            oldHash = _crypto.ComputeMasterPasswordHash(oldMasterKey, currentPassword);
        }
        catch (Exception ex) when (IsInvalidWrappedKey(ex))
        {
            throw new AccountOperationException("当前密码不正确。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldMasterKey);
        }

        // Rewrap the unchanged account key with the explicitly selected KDF.
        byte[] newMasterKey = await DeriveMasterKeyAsync(
            currentPassword,
            persisted.EffectiveMasterPasswordSalt,
            parameters.Type,
            parameters.Iterations,
            parameters.MemoryMiB,
            parameters.Parallelism,
            linked.Token).ConfigureAwait(false);

        string newHash;
        string wrappedKey;
        try
        {
            newHash = _crypto.ComputeMasterPasswordHash(newMasterKey, currentPassword);
            wrappedKey = ProtectUserKey(newMasterKey, userKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newMasterKey);
        }

        var kdfParams = new KdfParams(
            Kdf: (int)parameters.Type,
            KdfIterations: parameters.Iterations,
            KdfMemory: parameters.MemoryMiB,
            KdfParallelism: parameters.Parallelism);

        var request = new ChangeKdfRequest(
            NewMasterPasswordHash: newHash,
            Key: wrappedKey,
            AuthenticationData: new AuthData(
                Salt: persisted.EffectiveMasterPasswordSalt,
                Kdf: kdfParams,
                MasterPasswordAuthenticationHash: newHash),
            UnlockData: new UnlockData(
                Salt: persisted.EffectiveMasterPasswordSalt,
                Kdf: kdfParams,
                MasterKeyWrappedUserKey: wrappedKey,
                ContainedKeyId: userKey.KeyId),
            MasterPasswordHash: oldHash);

        _session.ThrowIfExpired(lease);
        var api = _clients.GetAuthenticated<IAccountApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.ChangeKdfAsync(request, linked.Token).ConfigureAwait(false);
        _session.ThrowIfExpired(lease);
        // CredentialChanged owns the eventual token/cache cleanup and navigation.
    }

    // -----------------------------------------------------------------------
    // VerifyMasterPasswordAsync:
    //   Reuses the password-change verification path for sensitive-action confirmation.
    //   The decrypted representation must match the key already held by this session.
    //   An invalid password or wrapped-key representation returns false.
    // -----------------------------------------------------------------------

    public async Task<bool> VerifyMasterPasswordAsync(string password, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        if (!_session.TryCopyUserKeyMaterial(lease, out var copiedKey) || copiedKey is null)
            throw new AccountOperationException("操作需要已解锁的 Vault，请先解锁后重试。");
        using var currentKey = copiedKey;

        if (!_tokenStore.TryLoad(out var persisted)
            || !_session.IsCurrent(lease, persisted))
            throw new AccountOperationException("持久化会话与当前账户不匹配，请重新登录后重试。");

        ct.ThrowIfCancellationRequested();

        byte[] masterKey = await DeriveMasterKeyAsync(
            password,
            persisted.EffectiveMasterPasswordSalt,
            persisted.KdfType,
            persisted.KdfIterations,
            persisted.KdfMemory,
            persisted.KdfParallelism,
            linked.Token).ConfigureAwait(false);

        bool valid;
        try
        {
            ValidateCurrentPassword(masterKey, persisted, currentKey);
            valid = true;
        }
        catch (Exception ex) when (IsInvalidWrappedKey(ex))
        {
            valid = false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }

        _session.ThrowIfExpired(lease);
        ct.ThrowIfCancellationRequested();
        if (valid)
            PinUnlockSessionPolicy.RecordMasterPasswordVerification(_session, _tokenStore, lease, persisted);
        return valid;
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Unwraps the saved account key using its declared security version, then compares it
    /// with the active key so a decryptable but unrelated wrapper cannot authenticate.
    /// </summary>
    private void ValidateCurrentPassword(
        byte[] masterKey,
        PersistedSession persisted,
        VaultUserKey expectedKey)
    {
        if (persisted.UserKeySecurityVersion != expectedKey.SecurityVersion)
            throw new CryptographicException("The saved and unlocked account key versions differ.");

        using var stretchedKey = _crypto.StretchMasterKey(masterKey);
        var encString = EncString.Parse(persisted.ProtectedUserKey);
        if (expectedKey.Legacy is { } legacy)
        {
            using var userKey = _crypto.DecryptUserKey(stretchedKey, encString);
            if (!CryptographicOperations.FixedTimeEquals(userKey.FullKey, legacy.FullKey))
                throw new CryptographicException("The password did not unlock the active account key.");
            return;
        }

        byte[]? encoded = _crypto.DecryptUserKeyMaterial(stretchedKey, encString);
        byte[]? actualTransport = null;
        byte[]? expectedTransport = null;
        try
        {
            using var key = VaultUserKey.ImportOwnedTransport(encoded, persisted.UserKeySecurityVersion);
            encoded = null;
            if (persisted.UserKeyId is not null
                && !string.Equals(persisted.UserKeyId, key.KeyId, StringComparison.Ordinal))
            {
                throw new CryptographicException("The password-unwrapped account key identifier is invalid.");
            }
            actualTransport = key.V2!.ExportTransport();
            expectedTransport = expectedKey.V2!.ExportTransport();
            if (!CryptographicOperations.FixedTimeEquals(actualTransport, expectedTransport))
                throw new CryptographicException("The password did not unlock the active account key.");
        }
        finally
        {
            if (encoded is not null)
                CryptographicOperations.ZeroMemory(encoded);
            if (actualTransport is not null)
                CryptographicOperations.ZeroMemory(actualTransport);
            if (expectedTransport is not null)
                CryptographicOperations.ZeroMemory(expectedTransport);
        }
    }

    private string ProtectUserKey(byte[] masterKey, VaultUserKey userKey)
    {
        if (userKey.Legacy is { } legacy)
            return _crypto.ProtectUserKey(masterKey, legacy).ToString();

        using var stretchedKey = _crypto.StretchMasterKey(masterKey);
        var transport = userKey.V2!.ExportTransport();
        try
        {
            return _crypto.Encrypt(transport, stretchedKey).ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transport);
        }
    }

    private static bool IsInvalidWrappedKey(Exception exception) => exception is
        CryptographicException or FormatException or ArgumentException or InvalidDataException
            or System.Formats.Cbor.CborContentException or OverflowException;

    private Task<byte[]> DeriveMasterKeyAsync(
        string password,
        string salt,
        KdfType type,
        int iterations,
        int? memory,
        int? parallelism,
        CancellationToken ct) => Task.Run(() =>
        {
            var key = _crypto.DeriveMasterKey(password, salt, type, iterations, memory, parallelism);
            if (!ct.IsCancellationRequested)
                return key;
            CryptographicOperations.ZeroMemory(key);
            throw new OperationCanceledException(ct);
        }, ct);

    private static string? ValidateNewPassword(
        string currentPassword,
        string newPassword,
        string? hint)
    {
        if (string.IsNullOrEmpty(currentPassword))
            throw new AccountOperationException("请输入当前主密码。");
        if (string.IsNullOrEmpty(newPassword)
            || newPassword.Length is < MasterPasswordPolicyRequirements.MinimumLength
                or > MasterPasswordPolicyRequirements.MaximumLength)
        {
            throw new AccountOperationException(
                $"新主密码长度必须为 {MasterPasswordPolicyRequirements.MinimumLength} 到 "
                + $"{MasterPasswordPolicyRequirements.MaximumLength} 个字符。");
        }
        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
            throw new AccountOperationException("新主密码不能与当前主密码相同。");

        var normalizedHint = string.IsNullOrWhiteSpace(hint) ? null : hint.Trim();
        if (normalizedHint is { Length: > MaximumPasswordHintLength }
            || normalizedHint?.Contains(newPassword, StringComparison.OrdinalIgnoreCase) == true
            || normalizedHint?.Contains(currentPassword, StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new AccountOperationException(
                "密码提示最多 50 个字符，且不能包含当前主密码或新主密码。");
        }

        return normalizedHint;
    }

    private void ValidateOrganizationPasswordPolicy(
        VaultSessionLease lease,
        MasterPasswordEvidence evidence)
    {
        if (!_session.TryRunIfUnlocked(lease, () =>
            {
                MasterPasswordPolicyRequirements? requirements;
                try
                {
                    requirements = MasterPasswordPolicyResolver.Resolve(_session);
                }
                catch (OrganizationPolicyException)
                {
                    throw new AccountOperationException("组织主密码策略数据无效，请同步密码库后重试。");
                }

                if (!_passwordPolicyEvaluator.Evaluate(requirements, evidence))
                {
                    throw new AccountOperationException(
                        "新主密码不符合组织的主密码策略。");
                }
            }))
        {
            throw new OperationCanceledException("Vault session lease expired.", lease.CancellationToken);
        }
    }
}
