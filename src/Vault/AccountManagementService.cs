using System.Net.Mail;
using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;

namespace Vault;

/// <summary>
/// Implements the Vaultwarden email and security-stamp contracts. An account-wide revocation
/// invalidates this device as well; no caller may describe it as other-devices-only revocation.
/// </summary>
public sealed class AccountManagementService(
    CryptoService crypto,
    IAccountService accounts,
    IVaultApiClientProvider clients,
    VaultSession session,
    ITokenStore tokenStore,
    VaultCacheService cache,
    ISessionTerminationSink? termination = null) : IAccountManagementService
{
    public AccountManagementAvailability GetAvailability()
    {
        var lease = session.CaptureLease();
        if (!session.IsUnlocked(lease))
            return new(false, "Unlock the vault before managing the account.");
        if (!tokenStore.TryLoad(out var persisted) || !session.IsCurrent(lease, persisted))
            return new(false, "Sign in again before changing the email address.");
        if (persisted.HasMasterPassword == false
            || persisted.UnlockMethod != PersistedSessionUnlockMethod.MasterPassword)
            return new(false, "Email changes require an account with a master password.");
        if (persisted.UserKeySecurityVersion != 1)
        {
            return new(false,
                "This server's email-change contract cannot update V2 account security state. "
                + "Use a compatible official account-management flow.");
        }
        return new(true, null);
    }

    public async Task RequestEmailChangeAsync(
        string currentPassword, string newEmail, CancellationToken ct = default)
    {
        var (lease, persisted) = CaptureEmailChangeSession();
        var email = NormalizeNewEmail(newEmail, persisted.Email);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var hash = await GetVerifiedPasswordHashAsync(lease, persisted, currentPassword, linked.Token)
            .ConfigureAwait(false);
        session.ThrowIfExpired(lease);
        await clients.GetAuthenticated<IAccountManagementApiClient>(lease)
            .RequestEmailChangeAsync(new(hash, email), linked.Token).ConfigureAwait(false);
        session.ThrowIfExpired(lease);
    }

    public async Task<AccountSecurityChangeReceipt> ChangeEmailAsync(
        string currentPassword, string newEmail, string token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length > 256 || token.Any(char.IsControl))
            throw new AccountOperationException("The email-change code is invalid.");
        var (lease, persisted) = CaptureEmailChangeSession();
        var email = NormalizeNewEmail(newEmail, persisted.Email);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var oldHash = await GetVerifiedPasswordHashAsync(lease, persisted, currentPassword, linked.Token)
            .ConfigureAwait(false);
        if (!session.TryCopyUserKey(lease, out var userKey) || userKey is null)
            throw new AccountOperationException("The current account does not have a supported unlocked key.");
        using (userKey)
        {
            var masterKey = await DeriveMasterKeyAsync(
                currentPassword, email, persisted, linked.Token).ConfigureAwait(false);
            AccountEmailChangeRequest request;
            try
            {
                request = new(oldHash, email, crypto.ProtectUserKey(masterKey, userKey).ToString(),
                    crypto.ComputeMasterPasswordHash(masterKey, currentPassword), token.Trim());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
            session.ThrowIfExpired(lease);
            linked.Token.ThrowIfCancellationRequested();
            var api = clients.GetAuthenticated<IAccountManagementApiClient>(lease);
            return await SendSecurityChangeAsync(
                lease, () => api.ChangeEmailAsync(request, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    public async Task RequestEmailVerificationAsync(CancellationToken ct = default)
    {
        var lease = CaptureUnlockedSession();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        await clients.GetAuthenticated<IAccountManagementApiClient>(lease)
            .RequestEmailVerificationAsync(linked.Token).ConfigureAwait(false);
        session.ThrowIfExpired(lease);
    }

    public async Task VerifyEmailAsync(string token, CancellationToken ct = default)
    {
        var lease = CaptureUnlockedSession();
        var value = ParseVerificationToken(token, lease.AccountId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        await clients.GetAuthenticated<IAccountManagementApiClient>(lease)
            .VerifyEmailTokenAsync(new(lease.AccountId, value), linked.Token).ConfigureAwait(false);
        session.ThrowIfExpired(lease);
    }

    public async Task<AccountSecurityChangeReceipt> RevokeAllSessionsAsync(
        string currentPassword, CancellationToken ct = default)
    {
        var lease = CaptureUnlockedSession();
        if (!tokenStore.TryLoad(out var persisted) || !session.IsCurrent(lease, persisted))
            throw new AccountOperationException("Sign in again before revoking sessions.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var hash = await GetVerifiedPasswordHashAsync(lease, persisted, currentPassword, linked.Token)
            .ConfigureAwait(false);
        session.ThrowIfExpired(lease);
        linked.Token.ThrowIfCancellationRequested();
        var api = clients.GetAuthenticated<IAccountManagementApiClient>(lease);
        return await SendSecurityChangeAsync(
            lease, () => api.RevokeAllSessionsAsync(new(hash), CancellationToken.None)).ConfigureAwait(false);
    }

    private async Task<AccountSecurityChangeReceipt> SendSecurityChangeAsync(
        VaultSessionLease lease, Func<Task> send)
    {
        var outcome = AccountSecurityChangeOutcome.Confirmed;
        try
        {
            // After dispatch, cancellation cannot establish whether the server committed. The
            // control-plane HTTP deadline still bounds this wait. Never automatically replay it.
            await send().ConfigureAwait(false);
        }
        catch (VaultWriteException exception) when (exception.StatusCode is { } status
            && (int)status is >= 400 and < 500 && (int)status != 408)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            outcome = AccountSecurityChangeOutcome.Uncertain;
        }

        VaultSessionLease? invalidated = null;
        var cleanupSucceeded = true;
        var claimed = session.TryCommit(lease, () =>
        {
            invalidated = session.Lock();
            try
            {
                cleanupSucceeded &= tokenStore.Remove(lease.AccountId, lease.Endpoints);
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                cleanupSucceeded = false;
            }
            try
            {
                cache.Clear(lease.AccountId, lease.Endpoints);
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                cleanupSucceeded = false;
            }
        });
        if (claimed && invalidated is not null)
        {
            termination?.Request(new(SessionTerminationReason.RemoteLogout, invalidated));
        }
        return new(outcome, lease, invalidated, claimed && cleanupSucceeded);
    }

    private (VaultSessionLease Lease, PersistedSession Persisted) CaptureEmailChangeSession()
    {
        var lease = CaptureUnlockedSession();
        var availability = GetAvailability();
        if (!availability.CanChangeEmail)
            throw new AccountOperationException(availability.EmailChangeUnavailableReason!);
        if (!tokenStore.TryLoad(out var persisted) || !session.IsCurrent(lease, persisted))
            throw new AccountOperationException("The saved account changed. Sign in again.");
        return (lease, persisted);
    }

    private VaultSessionLease CaptureUnlockedSession()
    {
        var lease = session.CaptureLease();
        if (!session.IsUnlocked(lease) || session.IsDemo)
            throw new AccountOperationException("Unlock a signed-in account before managing it.");
        return lease;
    }

    private async Task<string> GetVerifiedPasswordHashAsync(
        VaultSessionLease lease, PersistedSession persisted, string password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 1024)
            throw new AccountOperationException("Enter the current master password.");
        if (persisted.HasMasterPassword == false)
            throw new AccountOperationException("This account does not have a master password.");
        if (!await accounts.VerifyMasterPasswordAsync(password, ct).ConfigureAwait(false))
            throw new AccountOperationException("The current master password is incorrect.");
        var masterKey = await DeriveMasterKeyAsync(
            password, persisted.EffectiveMasterPasswordSalt, persisted, ct).ConfigureAwait(false);
        try
        {
            session.ThrowIfExpired(lease);
            ct.ThrowIfCancellationRequested();
            return crypto.ComputeMasterPasswordHash(masterKey, password);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private Task<byte[]> DeriveMasterKeyAsync(
        string password, string salt, PersistedSession persisted, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var key = crypto.DeriveMasterKey(password, salt, persisted.KdfType,
            persisted.KdfIterations, persisted.KdfMemory, persisted.KdfParallelism);
        if (!ct.IsCancellationRequested)
            return key;
        CryptographicOperations.ZeroMemory(key);
        throw new OperationCanceledException(ct);
    }, ct);

    private static string NormalizeNewEmail(string value, string oldEmail)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length is 0 or > 254 || normalized.Any(char.IsControl)
            || !MailAddress.TryCreate(normalized, out var parsed)
            || !string.Equals(parsed.Address, normalized, StringComparison.Ordinal))
            throw new AccountOperationException("Enter a valid new email address.");
        if (string.Equals(normalized, oldEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new AccountOperationException("The new email address must differ from the current address.");
        return normalized;
    }

    public static string ParseVerificationToken(string input, string expectedAccountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var value = input.Trim();
        if (value.Length > 24 * 1024 || value.Any(char.IsControl))
            throw new AccountOperationException("The verification token is invalid.");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return value.Length <= 16 * 1024 ? value
                : throw new AccountOperationException("The verification token is too large.");
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new AccountOperationException("Paste the email verification token or its web link.");
        var query = uri.Query.Length > 1 ? uri.Query[1..] : string.Empty;
        var fragment = uri.Fragment.TrimStart('#');
        var fragmentQuery = fragment.IndexOf('?');
        if (fragmentQuery >= 0)
            query += "&" + fragment[(fragmentQuery + 1)..];
        string? token = null;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length != 2)
                continue;
            var name = Uri.UnescapeDataString(pieces[0]);
            var decoded = Uri.UnescapeDataString(pieces[1]);
            if (name.Equals("userId", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(decoded, expectedAccountId, StringComparison.Ordinal))
                throw new AccountOperationException("The verification link belongs to another account.");
            if (name.Equals("token", StringComparison.OrdinalIgnoreCase))
            {
                if (token is not null)
                    throw new AccountOperationException("The verification link contains more than one token.");
                token = decoded;
            }
        }
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16 * 1024 || token.Any(char.IsControl))
            throw new AccountOperationException("The verification link does not contain a valid token.");
        return token;
    }

    private static bool IsStorageFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException
            or InvalidOperationException;
}
