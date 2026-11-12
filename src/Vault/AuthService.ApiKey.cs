using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Models;
using Core.Services;
using Core.Session;

namespace Vault;

public sealed partial class AuthService
{
    /// <summary>
    /// Authenticates a personal API key and unlocks its encrypted vault locally.
    /// The API grant deliberately has no refresh token. Only the normal encrypted
    /// offline-unlock state is saved; online expiry requires another explicit sign-in.
    /// </summary>
    public async Task<AuthResult> LoginWithApiKeyAsync(
        AuthenticationAttemptLease attempt,
        string serverUrl,
        string email,
        string clientId,
        string clientSecret,
        string masterPassword,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        PendingTwoFactorContext? context = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!_session.IsCurrent(attempt))
                return AuthenticationExpired();
            if (string.IsNullOrWhiteSpace(masterPassword))
                return SafeFailure("Enter the master password to decrypt this vault locally.");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, attempt.CancellationToken);
            var endpoints = ServerEndpoints.FromWebVault(NormalizeServerUrl(serverUrl));
            var normalizedEmail = MasterPasswordSaltPolicy.Resolve(email, explicitSalt: null);
            ConnectTokenRequest request;
            try
            {
                request = ConnectTokenRequest.UserApiKey(
                    clientId,
                    clientSecret,
                    NewDeviceIdentifier(),
                    DeviceName);
            }
            catch (ArgumentException)
            {
                return SafeFailure("Enter a valid personal API client ID and secret.");
            }
            var expectedAccountId = request.ClientId[5..];
            if (GetAccountDeletionUnlockBlock(expectedAccountId, endpoints) is { } deletionBlock)
                return deletionBlock;

            IAccountDeletionCoordinationLease coordination;
            try
            {
                coordination = await _accountDeletionCoordinationGate
                    .AcquireAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return AccountDeletionCoordinationUnavailable();
            }
            await using var coordinationScope = coordination;
            linked.Token.ThrowIfCancellationRequested();
            if (!_session.IsCurrent(attempt))
                return AuthenticationExpired();

            var result = await Identity(endpoints).ConnectTokenAsync(request, linked.Token)
                .ConfigureAwait(false);
            if (!_session.IsCurrent(attempt))
                return AuthenticationExpired();
            if (result is not ConnectTokenResult.Success success)
                return ToFailure(result);

            var token = success.Token;
            if (!AccessTokenSubjectReader.TryReadAccountId(token.AccessToken, out var accountId)
                || !Guid.TryParseExact(accountId, "D", out var accountUuid)
                || accountUuid != Guid.ParseExact(expectedAccountId, "D")
                || !AccessTokenSubjectReader.TryReadEmail(token.AccessToken, out var tokenEmail)
                || !string.Equals(
                    MasterPasswordSaltPolicy.NormalizeEmail(tokenEmail),
                    normalizedEmail,
                    StringComparison.Ordinal)
                || token.Scope != "api"
                || token.ExpiresIn <= 0
                || !string.IsNullOrEmpty(token.RefreshToken))
            {
                return InvalidResponseFailure();
            }
            if (token.UserDecryptionOptions?.HasMasterPassword == false)
            {
                return SafeFailure(
                    "API key sign-in requires an account with a master-password unlock method.");
            }

            // Resolve KDF and salt from the authenticated response. Neither the
            // master password nor its derived authentication hash is sent to the server.
            var unlock = ResolveTokenMasterPasswordUnlockMaterial(normalizedEmail, token);
            context = BuildLoginContext(
                attempt,
                endpoints,
                normalizedEmail,
                masterPassword,
                new PreloginResponse(
                    unlock.KdfType,
                    unlock.KdfIterations,
                    unlock.KdfMemory,
                    unlock.KdfParallelism,
                    Salt: unlock.MasterPasswordSalt),
                request.DeviceIdentifier);
            var ownedContext = context;
            context = null;
            return await CompleteLoginAsync(
                ownedContext,
                token with { RefreshToken = string.Empty },
                ct,
                isApiKeySession: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AbandonAuthenticationAttempt(attempt);
            throw;
        }
        catch (OperationCanceledException)
        {
            return AuthenticationExpired();
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            return !_session.IsCurrent(attempt)
                ? AuthenticationExpired()
                : ClassifyExpectedFailure(ex, ct);
        }
        finally
        {
            DisposePendingPasswordSecrets(context);
            _session.AbandonAuthenticationAttempt(attempt);
        }
    }
}
