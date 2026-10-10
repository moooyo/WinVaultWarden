using System.IO;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Crypto.PasswordStrength;
using Crypto.V2;

namespace Vault;

public sealed class AuthService : IAuthService
{
    private const string DeviceName = "WinVaultWarden";
    // Bitwarden TwoFactorProviderType.Email。启用邮箱 2FA 的账户在此值出现于 providers 时需登录期发码。
    private const int EmailTwoFactorProvider = (int)TwoFactorProviderType.Email;
    // Bitwarden TwoFactorProviderType.Remember。二次登录带此 provider + 记住令牌可免交互 2FA。
    private const int RememberTwoFactorProvider = (int)TwoFactorProviderType.Remember;
    private static readonly TimeSpan NewDeviceVerificationLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LoginWithDeviceQuarantineClearDelay =
        TimeSpan.FromMinutes(1);
    private static readonly Lazy<MasterPasswordPolicyEvaluator> DefaultPasswordPolicyEvaluator =
        new(() => new MasterPasswordPolicyEvaluator(
            new PasswordStrengthEvaluator(
                new Omnimatch(
                    new DictionaryMatcher(FrequencyDictionaries.Load())))));

    private readonly IVaultApiClientProvider _clients;
    private readonly CryptoService _crypto;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;
    private readonly VaultBootstrapper _bootstrapper;
    private readonly VaultCacheService _cache;
    private readonly ITokenRefresher _refresher;
    private readonly IKeyConnectorClient? _keyConnector;
    private readonly IDeviceTrustService? _deviceTrust;
    private readonly IDeviceIdentifierProvider? _deviceIdentifierProvider;
    private readonly V2AccountKeysValidator _v2AccountKeysValidator;
    private readonly ITdeV2JitProvisioningService? _tdeV2JitProvisioning;
    private readonly ITdeV2JitPendingStore? _tdeV2JitPendingStore;
    private readonly TimeProvider _timeProvider;
    private readonly MasterPasswordPolicyEvaluator _passwordPolicyEvaluator;
    private readonly TimeSpan _newDeviceVerificationLifetime;
    private readonly ILoginWithDeviceService? _loginWithDevice;
    private readonly ILoginWithDeviceQuarantineStore? _loginWithDeviceQuarantineStore;
    private readonly IAccountDeletionTombstoneStore? _accountDeletionTombstones;
    private readonly IAccountDeletionCoordinationGate _accountDeletionCoordinationGate;
    private readonly SemaphoreSlim _loginWithDeviceBeginGate = new(1, 1);
    private readonly object _pendingGate = new();
    private readonly object _authenticationTransitionGate = new();
    private PendingTwoFactorContext? _pendingTwoFactor;
    private PendingSsoTwoFactorContext? _pendingSsoTwoFactor;
    private PendingLoginWithDeviceContext? _pendingLoginWithDevice;
    private PendingLoginWithDeviceContext? _activeLoginWithDeviceExchange;
    private PendingLoginWithDeviceTwoFactorContext? _pendingLoginWithDeviceTwoFactor;
    private PendingTrustedDeviceAccountState? _pendingTrustedDeviceAccount;
    private PendingRequiredPasswordChangeContext? _pendingRequiredPasswordChange;
    private ITimer? _deviceVerificationExpiryTimer;

    public AuthService(
        IVaultApiClientProvider clients,
        CryptoService crypto,
        VaultSession session,
        ITokenStore tokenStore,
        VaultBootstrapper bootstrapper,
        VaultCacheService cache,
        ITokenRefresher refresher,
        IKeyConnectorClient? keyConnector = null,
        IDeviceTrustService? deviceTrust = null,
        IDeviceIdentifierProvider? deviceIdentifierProvider = null,
        V2AccountKeysValidator? v2AccountKeysValidator = null,
        ITdeV2JitProvisioningService? tdeV2JitProvisioning = null,
        ITdeV2JitPendingStore? tdeV2JitPendingStore = null,
        TimeProvider? timeProvider = null,
        MasterPasswordPolicyEvaluator? passwordPolicyEvaluator = null,
        TimeSpan? newDeviceVerificationLifetime = null,
        ILoginWithDeviceService? loginWithDevice = null,
        ILoginWithDeviceQuarantineStore? loginWithDeviceQuarantineStore = null,
        IAccountDeletionTombstoneStore? accountDeletionTombstones = null,
        IAccountDeletionCoordinationGate? accountDeletionCoordinationGate = null)
    {
        _clients = clients;
        _crypto = crypto;
        _session = session;
        _tokenStore = tokenStore;
        _bootstrapper = bootstrapper;
        _cache = cache;
        _refresher = refresher;
        _keyConnector = keyConnector;
        _deviceTrust = deviceTrust;
        _deviceIdentifierProvider = deviceIdentifierProvider;
        _v2AccountKeysValidator = v2AccountKeysValidator ?? new V2AccountKeysValidator();
        _tdeV2JitProvisioning = tdeV2JitProvisioning;
        _tdeV2JitPendingStore = tdeV2JitPendingStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _passwordPolicyEvaluator = passwordPolicyEvaluator
            ?? DefaultPasswordPolicyEvaluator.Value;
        _loginWithDevice = loginWithDevice;
        _loginWithDeviceQuarantineStore = loginWithDeviceQuarantineStore;
        _accountDeletionTombstones = accountDeletionTombstones;
        _accountDeletionCoordinationGate = accountDeletionCoordinationGate
            ?? NoopAccountDeletionCoordinationGate.Instance;
        _newDeviceVerificationLifetime =
            newDeviceVerificationLifetime ?? NewDeviceVerificationLifetime;
        if (_newDeviceVerificationLifetime <= TimeSpan.Zero
            || _newDeviceVerificationLifetime > NewDeviceVerificationLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(newDeviceVerificationLifetime));
        }
    }

    // 匿名 identity 角色按 origin 获取;登录/刷新/发码全部走匿名 transport,绝不携带 Bearer。
    private IIdentityApiClient Identity(ServerEndpoints endpoints) =>
        _clients.GetAnonymous<IIdentityApiClient>(endpoints);

    public Task RequestPasswordHintAsync(
        string serverUrl,
        string email,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ct.ThrowIfCancellationRequested();
        return Identity(ServerEndpoints.FromWebVault(serverUrl))
            .RequestPasswordHintAsync(email.Trim(), ct);
    }

    public AuthenticationAttemptLease BeginAuthenticationAttempt()
    {
        lock (_authenticationTransitionGate)
        {
            DisposePendingRequiredPasswordChange(endRestrictedSession: true);
            DisposePendingLoginWithDevice();
            var attempt = _session.BeginAuthenticationAttempt();
            DisposePendingTwoFactor();
            ClearPendingTrustedDeviceAccount();
            return attempt;
        }
    }

    public void AbandonAuthenticationAttempt(AuthenticationAttemptLease attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        _session.AbandonAuthenticationAttempt(attempt);
        DisposePendingTwoFactor(attempt);
        DisposePendingLoginWithDevice(
            attempt,
            removeQuarantineWhenNoIdentityExchangeIsInFlight: true);
    }

    public async Task<bool> IsLoginWithDeviceAvailableAsync(
        string serverUrl,
        string email,
        CancellationToken ct = default)
    {
        if (_loginWithDevice is null
            || _loginWithDeviceQuarantineStore is null
            || !TryGetStableDeviceIdentifier(out var deviceIdentifier))
        {
            return false;
        }

        try
        {
            var endpoints = ServerEndpoints.FromWebVault(
                NormalizeServerUrl(serverUrl));
            var normalizedEmail = MasterPasswordSaltPolicy.Resolve(
                email,
                explicitSalt: null);
            var quarantineBinding = new LoginWithDeviceQuarantineBinding(
                endpoints,
                normalizedEmail,
                deviceIdentifier);
            if (_loginWithDeviceQuarantineStore.Read(quarantineBinding).Status
                != LoginWithDeviceQuarantineStoreReadStatus.Absent)
            {
                return false;
            }
            return await _clients
                .GetAnonymous<IPublicAuthRequestApiClient>(endpoints)
                .GetKnownDeviceAsync(
                    normalizedEmail,
                    deviceIdentifier,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (
            (ex is LoginWithDeviceException or OperationCanceledException)
            || IsExpectedAuthException(ex)
            || IsExpectedPersistenceException(ex))
        {
            return false;
        }
    }

    public Task<LoginWithDeviceQuarantineInfo> GetLoginWithDeviceQuarantineInfoAsync(
        string serverUrl,
        string email,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_loginWithDeviceQuarantineStore is null
            || !TryGetStableDeviceIdentifier(out var deviceIdentifier))
        {
            return Task.FromResult<LoginWithDeviceQuarantineInfo>(
                new LoginWithDeviceQuarantineInfo.Unavailable());
        }

        try
        {
            var binding = new LoginWithDeviceQuarantineBinding(
                ServerEndpoints.FromWebVault(NormalizeServerUrl(serverUrl)),
                email,
                deviceIdentifier);
            return Task.FromResult(ToQuarantineInfo(
                _loginWithDeviceQuarantineStore.Read(binding)));
        }
        catch (Exception ex) when (
            IsExpectedPersistenceException(ex) || IsExpectedAuthException(ex))
        {
            return Task.FromResult<LoginWithDeviceQuarantineInfo>(
                new LoginWithDeviceQuarantineInfo.Unavailable());
        }
    }

    public Task<LoginWithDeviceQuarantineClearResult> ClearLoginWithDeviceQuarantineAsync(
        string serverUrl,
        string email,
        LoginWithDeviceQuarantineInfo.Found expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ct.ThrowIfCancellationRequested();
        if (_loginWithDeviceQuarantineStore is null
            || !TryGetStableDeviceIdentifier(out var deviceIdentifier))
        {
            return Task.FromResult(new LoginWithDeviceQuarantineClearResult(
                LoginWithDeviceQuarantineClearStatus.Unavailable));
        }

        try
        {
            var binding = new LoginWithDeviceQuarantineBinding(
                ServerEndpoints.FromWebVault(NormalizeServerUrl(serverUrl)),
                email,
                deviceIdentifier);
            var read = _loginWithDeviceQuarantineStore.Read(binding);
            var status = ClassifyQuarantineForClear(read, expected, out var record);
            if (status is not null)
            {
                return Task.FromResult(new LoginWithDeviceQuarantineClearResult(
                    status.Value));
            }

            if (!record!.IsClearEligible(_timeProvider.GetUtcNow()))
            {
                return Task.FromResult(new LoginWithDeviceQuarantineClearResult(
                    LoginWithDeviceQuarantineClearStatus.NotEligible));
            }

            if (_loginWithDeviceQuarantineStore.Remove(
                    record,
                    LoginWithDeviceQuarantineRemovalReason.UserConfirmedAfterEligibility))
            {
                return Task.FromResult(new LoginWithDeviceQuarantineClearResult(
                    LoginWithDeviceQuarantineClearStatus.Cleared));
            }

            var afterRemoval = _loginWithDeviceQuarantineStore.Read(binding);
            var retryStatus = ClassifyQuarantineForClear(
                afterRemoval,
                expected,
                out var current);
            if (retryStatus is null
                && current is not null
                && !current.IsClearEligible(_timeProvider.GetUtcNow()))
            {
                retryStatus = LoginWithDeviceQuarantineClearStatus.NotEligible;
            }

            return Task.FromResult(new LoginWithDeviceQuarantineClearResult(
                retryStatus ?? LoginWithDeviceQuarantineClearStatus.Changed));
        }
        catch (Exception ex) when (
            IsExpectedPersistenceException(ex) || IsExpectedAuthException(ex))
        {
            return Task.FromResult(new LoginWithDeviceQuarantineClearResult(
                LoginWithDeviceQuarantineClearStatus.Unavailable));
        }
    }

    public async Task<AuthResult> BeginLoginWithDeviceAsync(
        AuthenticationAttemptLease attempt,
        string serverUrl,
        string email,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (_loginWithDevice is null || _loginWithDeviceQuarantineStore is null)
        {
            AbandonAuthenticationAttempt(attempt);
            return SafeFailure("Login with device is not available.");
        }
        if (!_session.IsCurrent(attempt))
            return AuthenticationExpired();
        if (!TryGetStableDeviceIdentifier(out var deviceIdentifier))
        {
            AbandonAuthenticationAttempt(attempt);
            return SafeFailure(
                "Login with device requires an existing registered device identifier.");
        }

        var gateAcquired = false;
        try
        {
            using var gateCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                attempt.CancellationToken);
            await _loginWithDeviceBeginGate.WaitAsync(gateCancellation.Token)
                .ConfigureAwait(false);
            gateAcquired = true;
            if (!_session.IsCurrent(attempt))
                return AuthenticationExpired();
            var endpoints = ServerEndpoints.FromWebVault(
                NormalizeServerUrl(serverUrl));
            var normalizedEmail = MasterPasswordSaltPolicy.Resolve(
                email,
                explicitSalt: null);
            var quarantineBinding = new LoginWithDeviceQuarantineBinding(
                endpoints,
                normalizedEmail,
                deviceIdentifier);
            if (_loginWithDeviceQuarantineStore.Read(quarantineBinding).Status
                != LoginWithDeviceQuarantineStoreReadStatus.Absent)
            {
                _session.AbandonAuthenticationAttempt(attempt);
                DisposePendingTwoFactor(attempt);
                DisposePendingLoginWithDevice(attempt);
                return SafeFailure(
                    "Login with device is blocked until the previous request state is verified or cleared.");
            }
            if (TryGetPendingLoginWithDevice(attempt, out var existing))
                return ToWaitingResult(existing);
            if (TryGetActiveLoginWithDeviceExchange(attempt, out var exchanging))
            {
                return ToPendingResult(
                    exchanging,
                    LoginWithDeviceUiStatus.Pending);
            }

            DisposePendingLoginWithDevice(attempt);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                attempt.CancellationToken);
            var begin = await _loginWithDevice.BeginAsync(
                    attempt,
                    endpoints,
                    normalizedEmail,
                    deviceIdentifier,
                    linked.Token)
                .ConfigureAwait(false);

            PendingLoginWithDeviceContext? prior;
            lock (_authenticationTransitionGate)
            {
                if (!_session.IsCurrent(attempt))
                {
                    _loginWithDevice.Cancel(attempt);
                    return AuthenticationExpired();
                }

                var pending = new PendingLoginWithDeviceContext(
                    attempt,
                    endpoints,
                    normalizedEmail,
                    deviceIdentifier,
                    quarantineBinding,
                    begin.RequestId,
                    begin.FingerprintPhrase,
                    begin.ExpiresAtUtc);
                lock (_pendingGate)
                {
                    prior = _pendingLoginWithDevice;
                    _pendingLoginWithDevice = pending;
                }

                prior?.Dispose();
                return ToWaitingResult(pending);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (gateAcquired)
                AbandonAuthenticationAttempt(attempt);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (gateAcquired)
                AbandonAuthenticationAttempt(attempt);
            return AuthenticationExpired();
        }
        catch (LoginWithDeviceException ex)
        {
            AbandonAuthenticationAttempt(attempt);
            return ex.Reason == LoginWithDeviceFailureReason.UnknownDevice
                ? SafeFailure(
                    "Login with device is available only on a device already registered to this account.")
                : InvalidResponseFailure();
        }
        catch (Exception ex) when (
            IsExpectedAuthException(ex) || IsExpectedPersistenceException(ex))
        {
            AbandonAuthenticationAttempt(attempt);
            return ClassifyExpectedFailure(ex, ct);
        }
        finally
        {
            if (gateAcquired)
                _loginWithDeviceBeginGate.Release();
        }
    }

    public async Task<AuthResult> PollLoginWithDeviceAsync(
        AuthenticationAttemptLease attempt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (_loginWithDevice is null)
            return SessionExpiredFailure();
        if (!TryAcquirePendingLoginWithDevice(
                attempt,
                out var pending,
                out var operation))
        {
            if (TryGetPendingLoginWithDevice(attempt, out var busy))
            {
                return ToPendingResult(
                    busy,
                    LoginWithDeviceUiStatus.Pending);
            }
            if (TryGetActiveLoginWithDeviceExchange(attempt, out var exchanging))
            {
                return ToPendingResult(
                    exchanging,
                    LoginWithDeviceUiStatus.Pending);
            }
            return SessionExpiredFailure();
        }
        if (!_session.IsCurrent(attempt))
        {
            operation.Dispose();
            DisposePendingLoginWithDevice(attempt);
            return SessionExpiredFailure();
        }

        using (operation)
        {
            LoginWithDeviceGrant? ownedGrant = null;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    ct,
                    attempt.CancellationToken);
                var poll = await _loginWithDevice.PollAsync(attempt, linked.Token)
                    .ConfigureAwait(false);
                ownedGrant = poll.Grant;
                if (!_session.IsCurrent(attempt) || pending.DisposeRequested)
                    return AuthenticationExpired();

                switch (poll.Status)
                {
                    case LoginWithDevicePollStatus.Pending:
                        return ToPendingResult(
                            pending,
                            LoginWithDeviceUiStatus.Pending);
                    case LoginWithDevicePollStatus.Denied:
                        CompleteLoginWithDeviceRequest(pending, cancelService: false);
                        _session.AbandonAuthenticationAttempt(attempt);
                        return ToPendingResult(
                            pending,
                            LoginWithDeviceUiStatus.Denied);
                    case LoginWithDevicePollStatus.Expired:
                        CompleteLoginWithDeviceRequest(pending, cancelService: false);
                        _session.AbandonAuthenticationAttempt(attempt);
                        return ToPendingResult(
                            pending,
                            LoginWithDeviceUiStatus.Expired);
                    case LoginWithDevicePollStatus.Approved
                        when ownedGrant is { } grant:
                        if (!TryClaimApprovedLoginWithDevice(pending))
                            return AuthenticationExpired();
                        ownedGrant = null;
                        return await ExchangeLoginWithDeviceGrantAsync(
                                pending,
                                grant,
                                twoFactor: null,
                                autoSendEmail: true,
                                ct)
                            .ConfigureAwait(false);
                    default:
                        CompleteLoginWithDeviceRequest(pending, cancelService: true);
                        _session.AbandonAuthenticationAttempt(attempt);
                        return InvalidResponseFailure();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CompleteLoginWithDeviceRequest(pending, cancelService: false);
                _session.AbandonAuthenticationAttempt(attempt);
                throw;
            }
            catch (OperationCanceledException)
            {
                CompleteLoginWithDeviceRequest(pending, cancelService: false);
                _session.AbandonAuthenticationAttempt(attempt);
                return AuthenticationExpired();
            }
            catch (LoginWithDeviceException ex)
            {
                if (ex.Reason is LoginWithDeviceFailureReason.NoPendingRequest
                    or LoginWithDeviceFailureReason.InvalidServerResponse)
                {
                    CompleteLoginWithDeviceRequest(pending, cancelService: true);
                    _session.AbandonAuthenticationAttempt(attempt);
                }
                return ex.Reason == LoginWithDeviceFailureReason.NoPendingRequest
                    ? SessionExpiredFailure()
                    : InvalidResponseFailure();
            }
            catch (Exception ex) when (IsExpectedAuthException(ex))
            {
                return !_session.IsCurrent(attempt)
                    ? AuthenticationExpired()
                    : ClassifyExpectedFailure(ex, ct);
            }
            finally
            {
                ownedGrant?.Dispose();
            }
        }
    }

    public LoginWithDeviceCancellationResult CancelLoginWithDevice(
        AuthenticationAttemptLease attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        PendingLoginWithDeviceContext? pending = null;
        PendingLoginWithDeviceContext? active = null;
        PendingLoginWithDeviceTwoFactorContext? twoFactor = null;
        var outcomeUncertain = false;
        lock (_pendingGate)
        {
            if (_pendingLoginWithDevice?.Binding.Attempt == attempt)
            {
                pending = _pendingLoginWithDevice;
                _pendingLoginWithDevice = null;
                pending.Dispose();
            }
            if (_activeLoginWithDeviceExchange?.Binding.Attempt == attempt)
            {
                active = _activeLoginWithDeviceExchange;
                _activeLoginWithDeviceExchange = null;
                outcomeUncertain |= active.IdentityExchangeStarted;
                active.Dispose();
            }
            if (_pendingLoginWithDeviceTwoFactor?.Binding.Attempt == attempt)
            {
                twoFactor = _pendingLoginWithDeviceTwoFactor;
                _pendingLoginWithDeviceTwoFactor = null;
                outcomeUncertain |= twoFactor.IdentityExchangeStarted;
                twoFactor.Dispose();
            }
        }

        _loginWithDevice?.Cancel(attempt);
        _session.AbandonAuthenticationAttempt(attempt);
        var operationContext = (PendingLoginWithDeviceOperationContext?)active
            ?? twoFactor;
        if (operationContext is not null
            && !RevokePromotedLoginWithDeviceSession(operationContext))
        {
            outcomeUncertain = true;
        }
        var quarantineRecord = pending?.QuarantineRecord
            ?? active?.QuarantineRecord
            ?? twoFactor?.QuarantineRecord;
        if (!outcomeUncertain
            && quarantineRecord is not null
            && !TryRemoveLoginWithDeviceQuarantine(
                quarantineRecord,
                LoginWithDeviceQuarantineRemovalReason.DefiniteOutcome))
        {
            outcomeUncertain = true;
        }
        return new LoginWithDeviceCancellationResult(
            outcomeUncertain
                ? LoginWithDeviceCancellationStatus.OutcomeUncertain
                : LoginWithDeviceCancellationStatus.Cancelled);
    }

    private async Task<AuthResult> ExchangeLoginWithDeviceGrantAsync(
        PendingLoginWithDeviceContext pending,
        LoginWithDeviceGrant grant,
        TwoFactorPayload? twoFactor,
        bool autoSendEmail,
        CancellationToken ct)
    {
        var binding = pending.Binding;
        var transferredToTwoFactor = false;
        var identityExchangeStarted = false;
        try
        {
            ValidateLoginWithDeviceGrant(binding, grant);
            if (_timeProvider.GetUtcNow() >= binding.ExpiresAtUtc)
            {
                return new AuthResult.LoginWithDevicePending(
                    binding.RequestId,
                    binding.FingerprintPhrase,
                    binding.ExpiresAtUtc,
                    LoginWithDeviceUiStatus.Expired);
            }

            IAccountDeletionCoordinationLease coordination;
            try
            {
                coordination = await _accountDeletionCoordinationGate
                    .AcquireAsync(ct)
                    .ConfigureAwait(false);
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

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                binding.Attempt.CancellationToken);
            var effectiveTwoFactor = twoFactor
                ?? TryLoadLoginWithDeviceRememberPayload(binding);
            var request = CreateLoginWithDeviceTokenRequest(
                binding,
                grant,
                effectiveTwoFactor);
            var quarantineRecord = TryArmLoginWithDeviceQuarantine(binding);
            if (quarantineRecord is null)
                return new AuthResult.LoginWithDeviceOutcomeUncertain();
            pending.SetQuarantineRecord(quarantineRecord);
            if (!pending.TryMarkIdentityExchangeStarted())
            {
                return TryRemoveLoginWithDeviceQuarantine(
                        quarantineRecord,
                        LoginWithDeviceQuarantineRemovalReason.DefiniteOutcome)
                    ? AuthenticationExpired()
                    : new AuthResult.LoginWithDeviceOutcomeUncertain();
            }
            identityExchangeStarted = true;
            var result = await Identity(binding.Endpoints).ConnectTokenAsync(
                    request,
                    linked.Token)
                .ConfigureAwait(false);
            if (!_session.IsCurrent(binding.Attempt))
                return new AuthResult.LoginWithDeviceOutcomeUncertain();

            switch (result)
            {
                case ConnectTokenResult.Success success:
                    return await CompleteLoginWithDeviceAsync(
                            binding,
                            grant,
                            pending,
                            success.Token,
                            ct)
                        .ConfigureAwait(false);
                case ConnectTokenResult.TwoFactorRequired twoFactorRequired:
                {
                    if (_timeProvider.GetUtcNow() >= binding.ExpiresAtUtc)
                    {
                        return TryRemoveLoginWithDeviceQuarantine(
                                quarantineRecord,
                                LoginWithDeviceQuarantineRemovalReason.DefiniteOutcome)
                            ? new AuthResult.LoginWithDevicePending(
                                binding.RequestId,
                                binding.FingerprintPhrase,
                                binding.ExpiresAtUtc,
                                LoginWithDeviceUiStatus.Expired)
                            : new AuthResult.LoginWithDeviceOutcomeUncertain();
                    }
                    var twoFactorContext = new PendingLoginWithDeviceTwoFactorContext(
                        binding,
                        grant,
                        quarantineRecord,
                        twoFactorRequired.Providers.ToArray(),
                        NormalizeProviderData(twoFactorRequired.ProviderData));
                    if (!twoFactorContext.TryAcquireOperation(out var setupOperation))
                        return AuthenticationExpired();
                    using (setupOperation)
                    {
                        PendingLoginWithDeviceTwoFactorContext? prior;
                        lock (_authenticationTransitionGate)
                        {
                            if (!_session.IsCurrent(binding.Attempt))
                                return AuthenticationExpired();
                            lock (_pendingGate)
                            {
                                prior = _pendingLoginWithDeviceTwoFactor;
                                _pendingLoginWithDeviceTwoFactor = twoFactorContext;
                            }
                        }
                        prior?.Dispose();
                        transferredToTwoFactor = true;
                        pending.ClearIdentityExchangeStarted();
                        twoFactorContext.ArmExpiryTimer(
                            _timeProvider,
                            binding.ExpiresAtUtc - _timeProvider.GetUtcNow(),
                            () => ExpireLoginWithDeviceTwoFactor(twoFactorContext));
                        if (twoFactorContext.DisposeRequested)
                        {
                            return new AuthResult.LoginWithDevicePending(
                                binding.RequestId,
                                binding.FingerprintPhrase,
                                binding.ExpiresAtUtc,
                                LoginWithDeviceUiStatus.Expired);
                        }

                        if (autoSendEmail
                            && twoFactorRequired.Providers.Contains(EmailTwoFactorProvider))
                        {
                            await TrySendLoginWithDeviceTwoFactorEmailAsync(
                                    twoFactorContext,
                                    ct)
                                .ConfigureAwait(false);
                        }
                        if (ct.IsCancellationRequested
                            || !_session.IsCurrent(binding.Attempt)
                            || twoFactorContext.DisposeRequested)
                        {
                            AbandonAuthenticationAttempt(binding.Attempt);
                            return AuthenticationExpired();
                        }

                        return new AuthResult.TwoFactorRequired(
                            twoFactorRequired.Providers,
                            twoFactorContext.ProviderData);
                    }
                }
                case ConnectTokenResult.DeviceVerificationRequired:
                    return TryRemoveLoginWithDeviceQuarantine(
                            quarantineRecord,
                            LoginWithDeviceQuarantineRemovalReason.DefiniteOutcome)
                        ? InvalidResponseFailure()
                        : new AuthResult.LoginWithDeviceOutcomeUncertain();
                case ConnectTokenResult.Error error
                    when IsLoginWithDeviceOutcomeUncertain(error):
                    return new AuthResult.LoginWithDeviceOutcomeUncertain();
                case ConnectTokenResult.Error error:
                    return TryRemoveLoginWithDeviceQuarantine(
                            quarantineRecord,
                            LoginWithDeviceQuarantineRemovalReason.DefiniteOutcome)
                        ? ToFailure(error)
                        : new AuthResult.LoginWithDeviceOutcomeUncertain();
                default:
                    return InvalidResponseFailure();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AbandonAuthenticationAttempt(binding.Attempt);
            return identityExchangeStarted
                ? new AuthResult.LoginWithDeviceOutcomeUncertain()
                : AuthenticationExpired();
        }
        catch (OperationCanceledException)
        {
            AbandonAuthenticationAttempt(binding.Attempt);
            return identityExchangeStarted
                ? new AuthResult.LoginWithDeviceOutcomeUncertain()
                : AuthenticationExpired();
        }
        catch (Exception ex) when (
            ex is LoginWithDeviceException || IsExpectedAuthException(ex))
        {
            AbandonAuthenticationAttempt(binding.Attempt);
            if (identityExchangeStarted)
                return new AuthResult.LoginWithDeviceOutcomeUncertain();
            return ex is LoginWithDeviceException
                ? InvalidResponseFailure()
                : ClassifyExpectedFailure(ex, ct);
        }
        finally
        {
            CompleteActiveLoginWithDeviceExchange(pending);
            if (!transferredToTwoFactor)
            {
                grant.Dispose();
                _loginWithDevice?.Cancel(binding.Attempt);
                _session.AbandonAuthenticationAttempt(binding.Attempt);
            }
        }
    }

    private async Task<AuthResult> SubmitLoginWithDeviceTwoFactorAsync(
        PendingLoginWithDeviceTwoFactorContext context,
        IDisposable operation,
        string code,
        int provider,
        bool rememberDevice,
        CancellationToken ct)
    {
        using (operation)
        {
            if (!_session.IsCurrent(context.Binding.Attempt)
                || context.DisposeRequested
                || _timeProvider.GetUtcNow() >= context.Binding.ExpiresAtUtc)
            {
                var quarantineRecord = context.QuarantineRecord;
                AbandonAuthenticationAttempt(context.Binding.Attempt);
                return quarantineRecord is not null
                    && TryRemoveLoginWithDeviceQuarantine(
                        quarantineRecord,
                        LoginWithDeviceQuarantineRemovalReason.DefiniteOutcome)
                            ? SessionExpiredFailure()
                            : new AuthResult.LoginWithDeviceOutcomeUncertain();
            }

            var identityExchangeStarted = false;
            try
            {
                IAccountDeletionCoordinationLease coordination;
                try
                {
                    coordination = await _accountDeletionCoordinationGate
                        .AcquireAsync(ct)
                        .ConfigureAwait(false);
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
                var quarantineRecord = context.QuarantineRecord;
                if (quarantineRecord is null)
                {
                    AbandonAuthenticationAttempt(context.Binding.Attempt);
                    return new AuthResult.LoginWithDeviceOutcomeUncertain();
                }
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    ct,
                    context.Binding.Attempt.CancellationToken);
                var request = CreateLoginWithDeviceTokenRequest(
                    context.Binding,
                    context.Grant,
                    new TwoFactorPayload(provider, code, rememberDevice));
                if (!context.TryMarkIdentityExchangeStarted())
                    return AuthenticationExpired();
                identityExchangeStarted = true;
                var result = await Identity(context.Binding.Endpoints)
                    .ConnectTokenAsync(
                        request,
                        linked.Token)
                    .ConfigureAwait(false);
                if (!_session.IsCurrent(context.Binding.Attempt)
                    || context.DisposeRequested)
                {
                    return new AuthResult.LoginWithDeviceOutcomeUncertain();
                }

                switch (result)
                {
                    case ConnectTokenResult.Success success:
                        try
                        {
                            return await CompleteLoginWithDeviceAsync(
                                    context.Binding,
                                    context.Grant,
                                    context,
                                    success.Token,
                                    ct)
                                .ConfigureAwait(false);
                        }
                        finally
                        {
                            DetachPendingLoginWithDeviceTwoFactor(context);
                        }
                    case ConnectTokenResult.TwoFactorRequired twoFactorRequired:
                        context.ClearIdentityExchangeStarted();
                        context.UpdateChallenge(
                            twoFactorRequired.Providers,
                            NormalizeProviderData(twoFactorRequired.ProviderData));
                        return new AuthResult.TwoFactorRequired(
                            twoFactorRequired.Providers,
                            context.ProviderData);
                    case ConnectTokenResult.DeviceVerificationRequired:
                        AbandonAuthenticationAttempt(context.Binding.Attempt);
                        return TryRemoveLoginWithDeviceQuarantine(
                                quarantineRecord,
                                LoginWithDeviceQuarantineRemovalReason.DefiniteOutcome)
                            ? InvalidResponseFailure()
                            : new AuthResult.LoginWithDeviceOutcomeUncertain();
                    case ConnectTokenResult.Error error
                        when IsLoginWithDeviceOutcomeUncertain(error):
                        AbandonAuthenticationAttempt(context.Binding.Attempt);
                        return new AuthResult.LoginWithDeviceOutcomeUncertain();
                    default:
                        if (IsSingleUseTwoFactorProvider(provider))
                        {
                            var removed = TryRemoveLoginWithDeviceQuarantine(
                                quarantineRecord,
                                LoginWithDeviceQuarantineRemovalReason.DefiniteOutcome);
                            AbandonAuthenticationAttempt(context.Binding.Attempt);
                            return removed
                                ? SessionExpiredFailure()
                                : new AuthResult.LoginWithDeviceOutcomeUncertain();
                        }
                        context.ClearIdentityExchangeStarted();
                        return ToFailure(result);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                AbandonAuthenticationAttempt(context.Binding.Attempt);
                return identityExchangeStarted
                    ? new AuthResult.LoginWithDeviceOutcomeUncertain()
                    : AuthenticationExpired();
            }
            catch (OperationCanceledException)
            {
                AbandonAuthenticationAttempt(context.Binding.Attempt);
                return identityExchangeStarted
                    ? new AuthResult.LoginWithDeviceOutcomeUncertain()
                    : AuthenticationExpired();
            }
            catch (Exception ex) when (IsExpectedAuthException(ex))
            {
                AbandonAuthenticationAttempt(context.Binding.Attempt);
                return identityExchangeStarted
                    ? new AuthResult.LoginWithDeviceOutcomeUncertain()
                    : ClassifyExpectedFailure(ex, ct);
            }
        }
    }

    private async Task<AuthResult> CompleteLoginWithDeviceAsync(
        LoginWithDeviceBinding binding,
        LoginWithDeviceGrant grant,
        PendingLoginWithDeviceOperationContext exchangeContext,
        TokenResponse token,
        CancellationToken ct)
    {
        if (!_session.IsCurrent(binding.Attempt))
            return new AuthResult.LoginWithDeviceOutcomeUncertain();
        if (string.IsNullOrWhiteSpace(token.RefreshToken)
            || !AccessTokenSubjectReader.TryReadAccountId(
                token.AccessToken,
                out var accountId))
        {
            return new AuthResult.LoginWithDeviceOutcomeUncertain();
        }
        if (GetAccountDeletionUnlockBlock(accountId, binding.Endpoints) is { } deletionBlock)
            return deletionBlock;
        if (AccessTokenSubjectReader.TryReadEmail(token.AccessToken, out var tokenEmail)
            && !string.Equals(
                MasterPasswordSaltPolicy.NormalizeEmail(tokenEmail),
                binding.Email,
                StringComparison.Ordinal))
        {
            return new AuthResult.LoginWithDeviceOutcomeUncertain();
        }

        if (token.ForcePasswordReset)
        {
            lock (_authenticationTransitionGate)
            {
                if (!_session.IsCurrent(binding.Attempt))
                    return AuthenticationExpired();
                if (!TryRemovePersistedAccountData(accountId, binding.Endpoints))
                {
                    return SafeFailure(
                        "The previous saved session could not be removed securely.");
                }
                return SafeFailure(
                    "This account must replace its temporary password. Sign in with the temporary password to continue.");
            }
        }

        MasterPasswordUnlockMaterial? loginUnlock;
        try
        {
            loginUnlock = ResolveOptionalTokenMasterPasswordUnlockMaterial(
                binding.Email,
                token);
            if (loginUnlock is null
                && !IsActiveTrustedDeviceOption(
                    token.UserDecryptionOptions?.TrustedDeviceOption))
            {
                return new AuthResult.LoginWithDeviceOutcomeUncertain();
            }
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            return new AuthResult.LoginWithDeviceOutcomeUncertain();
        }

        SymmetricCryptoKey? legacyUserKey = null;
        V2UserKey? v2UserKey = null;
        var wrappedPrivateKey = token.PrivateKey;
        var accountKeyKind = AccountKeyKind.Invalid;
        PersistedAccountCryptographicState? accountCryptographicState = null;
        try
        {
            accountKeyKind = ClassifyAccountKeys(token);
            switch (accountKeyKind, grant.UserKeySecurityVersion)
            {
                case (AccountKeyKind.LegacyV1, 1) when loginUnlock is null
                    || IsType2EncString(loginUnlock.ProtectedUserKey):
                    legacyUserKey = new SymmetricCryptoKey(grant.UserKey.ToArray());
                    break;
                case (AccountKeyKind.V2, 2) when token.AccountKeys is not null:
                {
                    var transport = grant.UserKey.ToArray();
                    try
                    {
                        v2UserKey = V2UserKey.ImportTransport(transport);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(transport);
                    }
                    if (loginUnlock?.UserKeyId is { } expectedUserKeyId
                        && !string.Equals(
                            expectedUserKeyId,
                            v2UserKey.KeyIdHex,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "The approved V2 user key does not match its contained key identifier.");
                    }
                    wrappedPrivateKey = _v2AccountKeysValidator
                        .Validate(token.AccountKeys, v2UserKey)
                        .WrappedPrivateKey;
                    break;
                }
                default:
                    return new AuthResult.LoginWithDeviceOutcomeUncertain();
            }
            accountCryptographicState = CaptureAccountCryptographicState(
                token,
                accountKeyKind);
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            legacyUserKey?.Dispose();
            v2UserKey?.Dispose();
            return new AuthResult.LoginWithDeviceOutcomeUncertain();
        }

        var rememberedTwoFactorToken = ResolveLoginWithDeviceRememberToken(
            binding,
            token);

        VaultSessionLease? lease;
        lock (_authenticationTransitionGate)
        {
            if (!_session.TryBeginAccountSession(
                    binding.Attempt,
                    accountId,
                    binding.Email,
                    binding.Endpoints,
                    out lease))
            {
                legacyUserKey?.Dispose();
                v2UserKey?.Dispose();
                return new AuthResult.LoginWithDeviceOutcomeUncertain();
            }
            if (!exchangeContext.TrySetPromotedLease(lease!))
            {
                legacyUserKey?.Dispose();
                v2UserKey?.Dispose();
                _session.TryCommit(lease!, _session.Logout);
                return new AuthResult.LoginWithDeviceOutcomeUncertain();
            }
        }

        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            accountId,
            binding.Endpoints,
            lease!.Generation,
            binding.Email,
            binding.DeviceIdentifier,
            token.RefreshToken,
            loginUnlock?.ProtectedUserKey ?? string.Empty,
            loginUnlock?.KdfType ?? token.Kdf,
            loginUnlock?.KdfIterations ?? token.KdfIterations,
            loginUnlock?.KdfMemory ?? token.KdfMemory,
            loginUnlock?.KdfParallelism ?? token.KdfParallelism,
            loginUnlock?.MasterPasswordSalt
                ?? MasterPasswordSaltPolicy.NormalizeEmail(binding.Email),
            loginUnlock is null
                ? PersistedSessionUnlockMethod.TrustedDeviceSso
                : PersistedSessionUnlockMethod.MasterPassword)
        {
            TwoFactorToken = rememberedTwoFactorToken,
            HasMasterPassword = token.UserDecryptionOptions?.HasMasterPassword,
            SupportsDeviceTrust = token.UserDecryptionOptions?.TrustedDeviceOption is not null,
            UserKeySecurityVersion = grant.UserKeySecurityVersion,
            UserKeyId = v2UserKey?.KeyIdHex,
            AccountCryptographicState = accountCryptographicState,
        };

        var transferred = false;
        var cancelledDuringPromotion = false;
        var sessionPersisted = false;
        try
        {
            var committed = false;
            lock (_authenticationTransitionGate)
            {
                committed = _session.TryCommit(lease, () =>
                {
                    if (exchangeContext.DisposeRequested)
                    {
                        cancelledDuringPromotion = true;
                        return;
                    }
                    _tokenStore.Save(persisted);
                    sessionPersisted = true;
                    if (exchangeContext.DisposeRequested
                        || _timeProvider.GetUtcNow() >= binding.ExpiresAtUtc)
                    {
                        cancelledDuringPromotion = true;
                        return;
                    }
                    _session.SetTokens(token.AccessToken, token.RefreshToken);
                    if (v2UserKey is not null)
                        _session.SetUnlockedKey(v2UserKey);
                    else
                        _session.SetUnlockedKey(legacyUserKey!);
                    _session.SetEncryptedPrivateKey(wrappedPrivateKey);
                    _deviceTrust?.SetCurrentSessionCapability(
                        lease,
                        token.UserDecryptionOptions?.TrustedDeviceOption is not null);
                    transferred = true;
                    if (exchangeContext.DisposeRequested
                        || _timeProvider.GetUtcNow() >= binding.ExpiresAtUtc)
                    {
                        cancelledDuringPromotion = true;
                    }
                });
            }
            if (!committed)
            {
                _ = RevokePromotedLoginWithDeviceSession(exchangeContext);
                return new AuthResult.LoginWithDeviceOutcomeUncertain();
            }
            if (cancelledDuringPromotion
                || !exchangeContext.TryFinalizeCommittedPromotion(
                    _timeProvider.GetUtcNow(),
                    binding.ExpiresAtUtc))
            {
                var revoked = RevokePromotedLoginWithDeviceSession(exchangeContext);
                if (revoked
                    && sessionPersisted
                    && exchangeContext.QuarantineRecord is { } invalidatedRecord)
                {
                    _ = TryRemoveLoginWithDeviceQuarantine(
                        invalidatedRecord,
                        LoginWithDeviceQuarantineRemovalReason.SessionPersisted);
                }
                return new AuthResult.LoginWithDeviceOutcomeUncertain();
            }
        }
        catch (Exception ex) when (
            IsExpectedPersistenceException(ex) || IsExpectedAuthException(ex))
        {
            _ = RevokePromotedLoginWithDeviceSession(exchangeContext);
            return new AuthResult.LoginWithDeviceOutcomeUncertain();
        }
        finally
        {
            if (!transferred)
            {
                legacyUserKey?.Dispose();
                v2UserKey?.Dispose();
            }
        }

        exchangeContext.ClearIdentityExchangeStarted();
        if (!exchangeContext.DisposeRequested
            && exchangeContext.QuarantineRecord is { } quarantineRecord)
        {
            _ = TryRemoveLoginWithDeviceQuarantine(
                quarantineRecord,
                LoginWithDeviceQuarantineRemovalReason.SessionPersisted);
        }

        var fromCache = TryLoadCurrentVaultCache(lease);
        using var sessionLinked = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            lease.CancellationToken);
        try
        {
            await _bootstrapper.BootstrapAsync(lease, sessionLinked.Token)
                .ConfigureAwait(false);
            if (exchangeContext.DisposeRequested)
            {
                _ = RevokePromotedLoginWithDeviceSession(exchangeContext);
                return new AuthResult.LoginWithDeviceOutcomeUncertain();
            }
            return _session.IsUnlocked(lease)
                ? new AuthResult.Success(lease)
                : SessionExpiredFailure();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await LockAsync(lease, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            return exchangeContext.DisposeRequested
                ? new AuthResult.LoginWithDeviceOutcomeUncertain()
                : SessionExpiredFailure();
        }
        catch (Exception ex) when (
            ex is HttpRequestException or VaultApiException
            || RequestTimeoutClassifier.IsTimeout(ex, ct))
        {
            return FinishUnlockOffline(lease, fromCache);
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            await LockAsync(lease, CancellationToken.None).ConfigureAwait(false);
            return SafeFailure(
                "Authentication succeeded, but the vault could not be loaded.");
        }
    }

    private async Task TrySendLoginWithDeviceTwoFactorEmailAsync(
        PendingLoginWithDeviceTwoFactorContext context,
        CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                context.Binding.Attempt.CancellationToken);
            await Identity(context.Binding.Endpoints)
                .SendTwoFactorEmailLoginAsync(
                    CreateLoginWithDeviceEmailRequest(context),
                    linked.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (
            (ex is OperationCanceledException or ObjectDisposedException)
            || IsExpectedAuthException(ex))
        {
        }
    }

    public Task<AuthResult> LoginAsync(
        string serverUrl,
        string email,
        string masterPassword,
        CancellationToken ct = default) =>
        LoginAsync(BeginAuthenticationAttempt(), serverUrl, email, masterPassword, ct);

    public async Task<AuthResult> LoginAsync(
        AuthenticationAttemptLease attempt,
        string serverUrl,
        string email,
        string masterPassword,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        PendingTwoFactorContext? context = null;
        try
        {
            if (!_session.IsCurrent(attempt))
                return AuthenticationExpired();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, attempt.CancellationToken);
            var endpoints = ServerEndpoints.FromWebVault(NormalizeServerUrl(serverUrl));
            var api = Identity(endpoints);
            var normalizedEmail = MasterPasswordSaltPolicy.Resolve(email, explicitSalt: null);

            // prelogin/token 返回前不得改动当前(可能属于账户 A 的)会话状态,也不得开启 B 的 lease。
            var prelogin = await api.PreloginAsync(normalizedEmail, linked.Token);
            if (!_session.IsCurrent(attempt))
                return AuthenticationExpired();

            IAccountDeletionCoordinationLease coordination;
            try
            {
                coordination = await _accountDeletionCoordinationGate
                    .AcquireAsync(ct)
                    .ConfigureAwait(false);
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

            // 同一账户二次登录:复用上次的 device identifier(服务端把"记住此设备"令牌按 device 存储,
            // 换新 id 会令令牌失效),并带上已存的 remember 令牌免交互 2FA。仅在同 origin + 同 email 下复用。
            var deviceIdentifier = NewDeviceIdentifier();
            TwoFactorPayload? rememberPayload = null;
            if (_tokenStore.TryLoad(out var existing)
                && existing.Endpoints == endpoints
                && string.Equals(
                    MasterPasswordSaltPolicy.NormalizeEmail(existing.Email),
                    normalizedEmail,
                    StringComparison.Ordinal))
            {
                if (_deviceIdentifierProvider is null)
                    deviceIdentifier = existing.DeviceIdentifier;
                if (string.Equals(
                        deviceIdentifier,
                        existing.DeviceIdentifier,
                        StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(existing.TwoFactorToken))
                    rememberPayload = new TwoFactorPayload(RememberTwoFactorProvider, existing.TwoFactorToken, false);
            }

            context = BuildLoginContext(attempt, endpoints, normalizedEmail, masterPassword, prelogin, deviceIdentifier);
            var result = await api.ConnectTokenAsync(ConnectTokenRequest.Password(
                normalizedEmail,
                context.PasswordHash,
                context.DeviceIdentifier,
                DeviceName,
                rememberPayload), linked.Token);

            if (!_session.IsCurrent(attempt))
                return AuthenticationExpired();

            var ownedContext = context;
            context = null;
            return await HandleLoginResultAsync(
                ownedContext,
                result,
                ct,
                autoSendEmail: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AbandonAuthenticationAttempt(attempt);
            throw;
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            AbandonAuthenticationAttempt(attempt);
            return ClassifyExpectedFailure(ex, ct);
        }
        finally
        {
            DisposePendingPasswordSecrets(context);
        }
    }

    public async Task<AuthResult> LoginWithSsoAsync(
        AuthenticationAttemptLease attempt,
        string serverUrl,
        string email,
        SsoAuthorizationCode authorization,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(authorization);
        try
        {
            if (!_session.IsCurrent(attempt))
                return AuthenticationExpired();

            ValidateSsoAuthorization(authorization);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, attempt.CancellationToken);
            var endpoints = ServerEndpoints.FromWebVault(NormalizeServerUrl(serverUrl));
            var normalizedEmail = MasterPasswordSaltPolicy.Resolve(email, explicitSalt: null);

            IAccountDeletionCoordinationLease coordination;
            try
            {
                coordination = await _accountDeletionCoordinationGate
                    .AcquireAsync(ct)
                    .ConfigureAwait(false);
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

            var deviceIdentifier = NewDeviceIdentifier();
            TwoFactorPayload? rememberPayload = null;
            if (_tokenStore.TryLoad(out var existing)
                && existing.Endpoints == endpoints
                && string.Equals(
                    MasterPasswordSaltPolicy.NormalizeEmail(existing.Email),
                    normalizedEmail,
                    StringComparison.Ordinal))
            {
                if (_deviceIdentifierProvider is null)
                    deviceIdentifier = existing.DeviceIdentifier;
                if (string.Equals(
                        deviceIdentifier,
                        existing.DeviceIdentifier,
                        StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(existing.TwoFactorToken))
                    rememberPayload = new TwoFactorPayload(RememberTwoFactorProvider, existing.TwoFactorToken, false);
            }

            var context = new PendingSsoTwoFactorContext(
                attempt,
                endpoints,
                normalizedEmail,
                deviceIdentifier,
                authorization.Code,
                authorization.CodeVerifier,
                authorization.RedirectUri,
                authorization.OrganizationIdentifier,
                Array.Empty<int>(),
                new Dictionary<int, string>(),
                null,
                null);
            var result = await Identity(endpoints).ConnectTokenAsync(
                CreateSsoTokenRequest(context, rememberPayload),
                linked.Token);

            if (!_session.IsCurrent(attempt))
                return AuthenticationExpired();

            return await HandleSsoLoginResultAsync(
                context,
                result,
                ct,
                autoSendEmail: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AbandonAuthenticationAttempt(attempt);
            throw;
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            AbandonAuthenticationAttempt(attempt);
            return ClassifyExpectedFailure(ex, ct);
        }
    }

    public Task<AuthResult> SubmitTwoFactorAsync(
        string code,
        int provider,
        CancellationToken ct = default,
        bool rememberDevice = true)
    {
        var attempt = GetPendingTwoFactorAttempt();
        return attempt is null
            ? Task.FromResult<AuthResult>(SessionExpiredFailure())
            : SubmitTwoFactorAsync(attempt, code, provider, ct, rememberDevice);
    }

    public async Task<AuthResult> SubmitTwoFactorAsync(
        AuthenticationAttemptLease attempt,
        string code,
        int provider,
        CancellationToken ct = default,
        bool rememberDevice = true)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (TryAcquirePendingLoginWithDeviceTwoFactor(
                attempt,
                out var loginWithDeviceContext,
                out var loginWithDeviceOperation))
        {
            return await SubmitLoginWithDeviceTwoFactorAsync(
                    loginWithDeviceContext,
                    loginWithDeviceOperation,
                    code,
                    provider,
                    rememberDevice,
                    ct)
                .ConfigureAwait(false);
        }
        if (TryGetPendingSsoTwoFactor(attempt, out var ssoContext))
            return await SubmitSsoTwoFactorAsync(
                ssoContext,
                code,
                provider,
                rememberDevice,
                ct);

        if (!TryGetPendingTwoFactor(attempt, out var context)
            || context.RequiresDeviceVerification
            || !_session.IsCurrent(attempt))
            return SessionExpiredFailure();

        try
        {
            IAccountDeletionCoordinationLease coordination;
            try
            {
                coordination = await _accountDeletionCoordinationGate
                    .AcquireAsync(ct)
                    .ConfigureAwait(false);
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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, attempt.CancellationToken);
            var result = await Identity(context.Endpoints).ConnectTokenAsync(ConnectTokenRequest.Password(
                context.Email,
                context.PasswordHash,
                context.DeviceIdentifier,
                DeviceName,
                new TwoFactorPayload(provider, code, rememberDevice)), linked.Token);

            if (!_session.IsCurrent(attempt))
                return AuthenticationExpired();

            var handled = await HandleLoginResultAsync(
                context with { Providers = Array.Empty<int>() },
                result,
                ct);
            if (IsSingleUseTwoFactorProvider(provider)
                && handled is AuthResult.Failure)
            {
                AbandonAuthenticationAttempt(attempt);
                return SessionExpiredFailure();
            }
            return handled;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AbandonAuthenticationAttempt(attempt);
            throw;
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            if (!_session.IsCurrent(attempt))
            {
                AbandonAuthenticationAttempt(attempt);
                return SessionExpiredFailure();
            }
            if (IsSingleUseTwoFactorProvider(provider))
            {
                AbandonAuthenticationAttempt(attempt);
                return SessionExpiredFailure();
            }
            return ClassifyExpectedFailure(ex, ct);
        }
    }

    public async Task<AuthResult> SubmitNewDeviceVerificationAsync(
        AuthenticationAttemptLease attempt,
        string otp,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (string.IsNullOrWhiteSpace(otp) || otp.Length > 64)
        {
            return new AuthResult.Failure(
                "The device verification code is invalid.",
                AuthFailureKind.InvalidCredentials);
        }

        if (!TryGetPendingTwoFactor(attempt, out var context)
            || !context.RequiresDeviceVerification
            || !_session.IsCurrent(attempt))
        {
            return SessionExpiredFailure();
        }
        if (context.DeviceVerificationExpiresAt is not { } expiresAt
            || expiresAt <= _timeProvider.GetUtcNow())
        {
            AbandonAuthenticationAttempt(attempt);
            return SessionExpiredFailure();
        }

        try
        {
            IAccountDeletionCoordinationLease coordination;
            try
            {
                coordination = await _accountDeletionCoordinationGate
                    .AcquireAsync(ct)
                    .ConfigureAwait(false);
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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, attempt.CancellationToken);
            var result = await Identity(context.Endpoints).ConnectTokenAsync(
                ConnectTokenRequest.Password(
                    context.Email,
                    context.PasswordHash,
                    context.DeviceIdentifier,
                    DeviceName,
                    twoFactor: null,
                    newDeviceOtp: otp.Trim()),
                linked.Token);

            if (!_session.IsCurrent(attempt))
                return AuthenticationExpired();

            return await HandleLoginResultAsync(
                context with { RequiresDeviceVerification = false },
                result,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            return !_session.IsCurrent(attempt)
                ? AuthenticationExpired()
                : ClassifyExpectedFailure(ex, ct);
        }
    }

    public async Task<bool> ResendNewDeviceVerificationAsync(
        AuthenticationAttemptLease attempt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!TryGetPendingTwoFactor(attempt, out var context)
            || !context.RequiresDeviceVerification
            || !_session.IsCurrent(attempt))
        {
            return false;
        }
        if (context.DeviceVerificationExpiresAt is not { } expiresAt
            || expiresAt <= _timeProvider.GetUtcNow())
        {
            AbandonAuthenticationAttempt(attempt);
            return false;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, attempt.CancellationToken);
            await Identity(context.Endpoints).ResendNewDeviceOtpAsync(
                new ResendNewDeviceOtpRequest(context.Email, context.PasswordHash),
                linked.Token);
            return _session.IsCurrent(attempt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            return false;
        }
    }

    private async Task<AuthResult> SubmitSsoTwoFactorAsync(
        PendingSsoTwoFactorContext context,
        string code,
        int provider,
        bool rememberDevice,
        CancellationToken ct)
    {
        if (!_session.IsCurrent(context.Attempt))
            return SessionExpiredFailure();

        try
        {
            IAccountDeletionCoordinationLease coordination;
            try
            {
                coordination = await _accountDeletionCoordinationGate
                    .AcquireAsync(ct)
                    .ConfigureAwait(false);
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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, context.Attempt.CancellationToken);
            var result = await Identity(context.Endpoints).ConnectTokenAsync(
                CreateSsoTokenRequest(
                    context,
                    new TwoFactorPayload(provider, code, rememberDevice)),
                linked.Token);

            if (!_session.IsCurrent(context.Attempt))
                return AuthenticationExpired();

            var handled = await HandleSsoLoginResultAsync(
                context with { Providers = Array.Empty<int>() },
                result,
                ct);
            if (IsSingleUseTwoFactorProvider(provider)
                && handled is AuthResult.Failure)
            {
                AbandonAuthenticationAttempt(context.Attempt);
                return SessionExpiredFailure();
            }
            return handled;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AbandonAuthenticationAttempt(context.Attempt);
            throw;
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            if (!_session.IsCurrent(context.Attempt))
            {
                AbandonAuthenticationAttempt(context.Attempt);
                return SessionExpiredFailure();
            }
            if (IsSingleUseTwoFactorProvider(provider))
            {
                AbandonAuthenticationAttempt(context.Attempt);
                return SessionExpiredFailure();
            }
            return ClassifyExpectedFailure(ex, ct);
        }
    }

    public Task<AuthResult> UnlockAsync(
        string masterPassword,
        CancellationToken ct = default) =>
        UnlockAsync(BeginAuthenticationAttempt(), masterPassword, ct);

    public async Task<AuthResult> UnlockAsync(
        AuthenticationAttemptLease attempt,
        string masterPassword,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!_session.IsCurrent(attempt))
            return AuthenticationExpired();
        IAccountDeletionCoordinationLease coordination;
        try
        {
            coordination = await _accountDeletionCoordinationGate
                .AcquireAsync(ct)
                .ConfigureAwait(false);
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
        if (!_tokenStore.TryLoad(out var persisted) || string.IsNullOrEmpty(persisted.ProtectedUserKey))
        {
            AbandonAuthenticationAttempt(attempt);
            return new AuthResult.Failure(
                "No saved session.",
                AuthFailureKind.NoSavedSession);
        }
        if (GetAccountDeletionUnlockBlock(persisted) is { } deletionBlock)
        {
            AbandonAuthenticationAttempt(attempt);
            return deletionBlock;
        }
        VaultUserKey userKey;
        try
        {
            var stretchedKey = DeriveStretchedKey(
                masterPassword,
                persisted.EffectiveMasterPasswordSalt,
                persisted.KdfType,
                persisted.KdfIterations,
                persisted.KdfMemory,
                persisted.KdfParallelism);
            try
            {
                userKey = UnwrapUserKey(
                    stretchedKey,
                    persisted.ProtectedUserKey,
                    persisted.UserKeySecurityVersion,
                    persisted.UserKeyId);
            }
            finally
            {
                stretchedKey.Dispose();
            }
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            if (!_session.TryRestorePersistedSession(attempt, persisted, out _))
                return AuthenticationExpired();
            return ex is CryptographicException or FormatException
                ? new AuthResult.Failure(
                    "The master password could not unlock the stored key.",
                    AuthFailureKind.InvalidCredentials)
                : ClassifyExpectedFailure(ex, ct);
        }

        string? wrappedPrivateKey;
        try
        {
            wrappedPrivateKey = ValidateAccountCryptographicState(
                userKey,
                persisted.AccountCryptographicState);
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            userKey.Dispose();
            if (!_session.TryRestorePersistedSession(attempt, persisted, out _))
                return AuthenticationExpired();
            return new AuthResult.Failure(
                "The saved account cryptographic state could not be authenticated.",
                AuthFailureKind.InvalidResponse);
        }

        return await FinishLocalUnlockAsync(
            attempt,
            persisted,
            userKey,
            wrappedPrivateKey,
            ct);
    }

    public async Task<AuthResult> UnlockWithKeyConnectorAsync(
        AuthenticationAttemptLease attempt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!_session.IsCurrent(attempt))
            return AuthenticationExpired();
        IAccountDeletionCoordinationLease coordination;
        try
        {
            coordination = await _accountDeletionCoordinationGate
                .AcquireAsync(ct)
                .ConfigureAwait(false);
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
        if (_keyConnector is null
            || !_tokenStore.TryLoad(out var persisted)
            || persisted.UnlockMethod != PersistedSessionUnlockMethod.KeyConnector
            || string.IsNullOrWhiteSpace(persisted.KeyConnectorUrl)
            || string.IsNullOrWhiteSpace(persisted.ProtectedUserKey))
        {
            AbandonAuthenticationAttempt(attempt);
            return new AuthResult.Failure(
                "No Key Connector session is available.",
                AuthFailureKind.NoSavedSession);
        }
        if (GetAccountDeletionUnlockBlock(persisted) is { } deletionBlock)
        {
            AbandonAuthenticationAttempt(attempt);
            return deletionBlock;
        }
        if (!_session.TryRestorePersistedSession(attempt, persisted, out var lease))
            return AuthenticationExpired();

        var normalized = persisted with { Generation = lease!.Generation };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, lease.CancellationToken);
        try
        {
            try
            {
                if (!_session.TryCommit(lease, () =>
                {
                    _tokenStore.Save(normalized);
                    _session.SetRefreshTokenOnly(normalized.RefreshToken);
                }))
                    return SessionExpiredFailure();
            }
            catch (Exception ex) when (IsExpectedPersistenceException(ex))
            {
                _session.TryLockIfCurrent(lease);
                return SafeFailure(
                    "The authenticated session could not be stored securely.");
            }

            if (!await _refresher.TryRefreshAsync(lease, linked.Token).ConfigureAwait(false)
                || !_session.TryGetTokenSnapshot(lease, out var accessToken, out _)
                || string.IsNullOrWhiteSpace(accessToken))
            {
                return SafeFailure("Key Connector unlock requires a server connection.");
            }

            var masterKeyBytes = await _keyConnector.GetMasterKeyAsync(
                persisted.KeyConnectorUrl,
                accessToken,
                linked.Token).ConfigureAwait(false);
            var userKey = DecryptLegacyKeyConnectorUserKey(
                masterKeyBytes,
                persisted.ProtectedUserKey);
            var transferred = false;
            try
            {
                if (!_session.TryCommit(lease, () =>
                {
                    _session.SetUnlockedKey(userKey);
                    _deviceTrust?.SetCurrentSessionCapability(lease, supported: false);
                    transferred = true;
                }))
                    return SessionExpiredFailure();
            }
            finally
            {
                if (!transferred)
                    userKey.Dispose();
            }

            if (!_session.TryCopyUserKey(lease, out var cacheKey))
                return SessionExpiredFailure();
            bool fromCache;
            using (cacheKey)
                fromCache = _cache.TryLoadInto(lease, cacheKey!);

            try
            {
                await _bootstrapper.BootstrapAsync(lease, linked.Token).ConfigureAwait(false);
                return CompleteLocalUnlockSuccess(lease);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsExpectedAuthException(ex))
            {
                return FinishUnlockOffline(lease, fromCache);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await LockAsync(lease, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            await LockAsync(lease, CancellationToken.None).ConfigureAwait(false);
            return ClassifyExpectedFailure(ex, ct);
        }
    }

    public async Task<AuthResult> CompleteTrustedDeviceApprovalAsync(
        VaultSessionLease authenticatedSession,
        byte[] userKeyBytes,
        bool trustCurrentDevice,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authenticatedSession);
        ArgumentNullException.ThrowIfNull(userKeyBytes);
        IAccountDeletionCoordinationLease coordination;
        try
        {
            coordination = await _accountDeletionCoordinationGate
                .AcquireAsync(ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CryptographicOperations.ZeroMemory(userKeyBytes);
            throw;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(userKeyBytes);
            return AccountDeletionCoordinationUnavailable();
        }
        await using var coordinationScope = coordination;
        if (GetAccountDeletionUnlockBlock(
                authenticatedSession.AccountId,
                authenticatedSession.Endpoints) is { } deletionBlock)
        {
            CryptographicOperations.ZeroMemory(userKeyBytes);
            _session.TryLockIfCurrent(authenticatedSession);
            ClearPendingTrustedDeviceAccount(authenticatedSession);
            return deletionBlock;
        }

        PendingTrustedDeviceAccountState? pendingAccount = null;
        var claimed = _session.TryCommit(authenticatedSession, () =>
        {
            lock (_pendingGate)
            {
                if (_pendingTrustedDeviceAccount is { } pending
                    && pending.Lease.SameSessionAs(authenticatedSession))
                {
                    pendingAccount = pending;
                    _pendingTrustedDeviceAccount = null;
                }
            }
        });

        if (!claimed || pendingAccount is null)
        {
            CryptographicOperations.ZeroMemory(userKeyBytes);
            return SessionExpiredFailure();
        }

        SymmetricCryptoKey? legacyUserKey = null;
        V2UserKey? v2UserKey = null;
        byte[]? ownedUserKeyBytes = userKeyBytes;
        string? wrappedPrivateKey = null;
        try
        {
            if (pendingAccount.AccountKeyKind == AccountKeyKind.V2
                && pendingAccount.AccountKeys is not null)
            {
                v2UserKey = V2UserKey.ImportTransport(ownedUserKeyBytes);
                CryptographicOperations.ZeroMemory(ownedUserKeyBytes);
                ownedUserKeyBytes = null;
                if (pendingAccount.UserKeyId is not null
                    && !string.Equals(
                        pendingAccount.UserKeyId,
                        v2UserKey.KeyIdHex,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The approved V2 user key does not match its contained key identifier.");
                }
                wrappedPrivateKey = _v2AccountKeysValidator
                    .Validate(pendingAccount.AccountKeys, v2UserKey)
                    .WrappedPrivateKey;
            }
            else if (pendingAccount.AccountKeyKind == AccountKeyKind.LegacyV1
                && ownedUserKeyBytes.Length == 64)
            {
                legacyUserKey = new SymmetricCryptoKey(ownedUserKeyBytes);
                ownedUserKeyBytes = null;
            }
            else
            {
                throw new InvalidDataException(
                    "The approved user key does not match the authenticated account state.");
            }
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            if (ownedUserKeyBytes is not null)
                CryptographicOperations.ZeroMemory(ownedUserKeyBytes);
            legacyUserKey?.Dispose();
            v2UserKey?.Dispose();
            return InvalidResponseFailure();
        }

        var transferred = false;
        try
        {
            if (!_session.TryCommit(authenticatedSession, () =>
            {
                if (v2UserKey is not null)
                    _session.SetUnlockedKey(v2UserKey);
                else
                    _session.SetUnlockedKey(legacyUserKey!);
                if (wrappedPrivateKey is not null)
                    _session.SetEncryptedPrivateKey(wrappedPrivateKey);
                transferred = true;
            }))
            {
                return SessionExpiredFailure();
            }
        }
        finally
        {
            if (!transferred)
            {
                legacyUserKey?.Dispose();
                v2UserKey?.Dispose();
            }
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, authenticatedSession.CancellationToken);
        try
        {
            if (trustCurrentDevice)
            {
                if (_deviceTrust is null)
                    throw new DeviceTrustException(
                        "Trusted-device enrollment is unavailable.");
                await _deviceTrust.TrustCurrentDeviceAsync(linked.Token)
                    .ConfigureAwait(false);
            }

            await _bootstrapper.BootstrapAsync(authenticatedSession, linked.Token)
                .ConfigureAwait(false);
            return _session.IsUnlocked(authenticatedSession)
                ? new AuthResult.Success(authenticatedSession)
                : SessionExpiredFailure();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await LockAsync(authenticatedSession, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (
            IsExpectedAuthException(ex) || ex is DeviceTrustException)
        {
            await LockAsync(authenticatedSession, CancellationToken.None)
                .ConfigureAwait(false);
            return SafeFailure(
                trustCurrentDevice && ex is DeviceTrustException
                    ? "The device was approved, but its trust keys could not be stored."
                    : "The device was approved, but the vault could not be loaded.");
        }
    }

    public async Task<AuthResult> CompleteTdeV2JitProvisioningAsync(
        VaultSessionLease authenticatedSession,
        string organizationIdentifier,
        bool trustCurrentDevice,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authenticatedSession);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationIdentifier);
        if (_tdeV2JitProvisioning is null)
        {
            return new AuthResult.Failure(
                "V2 TDE JIT provisioning is unavailable.",
                AuthFailureKind.UnsupportedSsoDecryption);
        }

        try
        {
            IAccountDeletionCoordinationLease coordination;
            try
            {
                coordination = await _accountDeletionCoordinationGate
                    .AcquireAsync(ct)
                    .ConfigureAwait(false);
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
            if (GetAccountDeletionUnlockBlock(
                    authenticatedSession.AccountId,
                    authenticatedSession.Endpoints) is { } deletionBlock)
            {
                _session.TryLockIfCurrent(authenticatedSession);
                return deletionBlock;
            }

            var result = await _tdeV2JitProvisioning.ProvisionAsync(
                authenticatedSession,
                organizationIdentifier,
                trustCurrentDevice,
                ct).ConfigureAwait(false);
            return _session.IsUnlocked(result.Lease)
                ? new AuthResult.Success(result.Lease)
                : SessionExpiredFailure();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TdeV2JitProvisioningException ex)
        {
            return ex.Stage == TdeV2JitProvisioningStage.CapabilityCheck
                ? new AuthResult.Failure(
                    "This server has not enabled V2 TDE JIT provisioning for this organization.",
                    AuthFailureKind.UnsupportedSsoDecryption)
                : SafeFailure(
                    ex.AccountKeysMayHaveBeenCommitted
                        ? "Account-key provisioning may have partially completed. Sign in again to reconcile before retrying."
                        : "V2 TDE JIT provisioning could not be completed.");
        }
    }

    public Task<AuthResult> UnlockWithPinAsync(
        string pin,
        CancellationToken ct = default) =>
        UnlockWithPinAsync(BeginAuthenticationAttempt(), pin, ct);

    public async Task<AuthResult> UnlockWithPinAsync(
        AuthenticationAttemptLease attempt,
        string pin,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!_session.IsCurrent(attempt))
            return AuthenticationExpired();
        IAccountDeletionCoordinationLease coordination;
        try
        {
            coordination = await _accountDeletionCoordinationGate
                .AcquireAsync(ct)
                .ConfigureAwait(false);
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
        if (!_tokenStore.TryLoad(out var persisted) || string.IsNullOrEmpty(persisted.PinProtectedUserKey))
        {
            AbandonAuthenticationAttempt(attempt);
            return SafeFailure("未设置 PIN。");
        }
        if (GetAccountDeletionUnlockBlock(persisted) is { } deletionBlock)
        {
            AbandonAuthenticationAttempt(attempt);
            return deletionBlock;
        }

        if (persisted.UserKeySecurityVersion != 1)
        {
            AbandonAuthenticationAttempt(attempt);
            return SafeFailure(
                "PIN unlock is not available for this V2 account. Use the master password instead.");
        }

        SymmetricCryptoKey userKey;
        try
        {
            var stretchedKey = DeriveStretchedKey(
                pin, MasterPasswordSaltPolicy.NormalizeEmail(persisted.Email), persisted.KdfType,
                persisted.KdfIterations, persisted.KdfMemory, persisted.KdfParallelism);
            try
            {
                userKey = _crypto.DecryptUserKey(stretchedKey, EncString.Parse(persisted.PinProtectedUserKey));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return HandleWrongPin(attempt, persisted);
            }
            finally
            {
                // 拉伸子钥用完清零;存入 session 的 userKey 是独立数组,不受影响。
                stretchedKey.Dispose();
            }
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            AbandonAuthenticationAttempt(attempt);
            return ClassifyExpectedFailure(ex, ct);
        }

        // 成功解包 → 清零失败计数,经 normalized 记录在解锁门禁内落盘。
        return await FinishLocalUnlockAsync(
            attempt,
            persisted with { PinFailedAttempts = 0 },
            userKey,
            ct,
            isPinUnlock: true);
    }

    // PIN 错误:经恢复出的 locked lease 门禁写回失败计数/清 PIN,绝不在 TryCommit 外触碰 token store。
    private AuthResult HandleWrongPin(
        AuthenticationAttemptLease attempt,
        PersistedSession persisted)
    {
        if (!_session.TryRestorePersistedSession(attempt, persisted, out var lease))
            return AuthenticationExpired();
        var n = persisted.PinFailedAttempts + 1;
        if (n >= 5)
        {
            _session.TryCommit(lease!, () => _tokenStore.Save(persisted with
            {
                PinProtectedUserKey = null,
                PinFailedAttempts = 0,
                Generation = lease!.Generation,
            }));
            return new AuthResult.PinCleared("PIN 已因多次错误被清除，请用主密码解锁。");
        }
        _session.TryCommit(lease!, () => _tokenStore.Save(persisted with
        {
            PinFailedAttempts = n,
            Generation = lease!.Generation,
        }));
        return SafeFailure($"PIN 错误，还可尝试 {5 - n} 次。");
    }

    public Task<AuthResult> UnlockWithHelloAsync(
        byte[] signature,
        CancellationToken ct = default) =>
        UnlockWithHelloAsync(BeginAuthenticationAttempt(), signature, ct);

    public async Task<AuthResult> UnlockWithHelloAsync(
        AuthenticationAttemptLease attempt,
        byte[] signature,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!_session.IsCurrent(attempt))
        {
            CryptographicOperations.ZeroMemory(signature);
            return AuthenticationExpired();
        }
        IAccountDeletionCoordinationLease coordination;
        try
        {
            coordination = await _accountDeletionCoordinationGate
                .AcquireAsync(ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CryptographicOperations.ZeroMemory(signature);
            throw;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(signature);
            return AccountDeletionCoordinationUnavailable();
        }
        await using var coordinationScope = coordination;
        if (!_tokenStore.TryLoad(out var persisted) || string.IsNullOrEmpty(persisted.HelloProtectedUserKey))
        {
            CryptographicOperations.ZeroMemory(signature);
            AbandonAuthenticationAttempt(attempt);
            return SafeFailure("未启用 Windows Hello。");
        }
        if (GetAccountDeletionUnlockBlock(persisted) is { } deletionBlock)
        {
            CryptographicOperations.ZeroMemory(signature);
            AbandonAuthenticationAttempt(attempt);
            return deletionBlock;
        }
        if (persisted.UserKeySecurityVersion != 1)
        {
            CryptographicOperations.ZeroMemory(signature);
            AbandonAuthenticationAttempt(attempt);
            return SafeFailure(
                "Windows Hello unlock is not available for this V2 account. Use the master password instead.");
        }

        SymmetricCryptoKey userKey;
        try
        {
            using var helloKey = _crypto.DeriveKeyFromSecret(signature);
            try
            {
                userKey = _crypto.DecryptUserKey(helloKey, EncString.Parse(persisted.HelloProtectedUserKey));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Hello 凭据失效(重录/移除)或数据损坏 → 经恢复出的 locked lease 门禁清除 Hello,回退主密码。
                if (!_session.TryRestorePersistedSession(attempt, persisted, out var lease))
                    return AuthenticationExpired();
                _session.TryCommit(lease!, () => _tokenStore.Save(persisted with
                {
                    HelloProtectedUserKey = null,
                    HelloChallenge = null,
                    Generation = lease!.Generation,
                }));
                return SafeFailure("Windows Hello 解锁失败，请用主密码解锁。");
            }
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            AbandonAuthenticationAttempt(attempt);
            return ClassifyExpectedFailure(ex, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }

        return await FinishLocalUnlockAsync(attempt, persisted, userKey, ct);
    }

    // 主密码/PIN/Hello 三入口共用:恢复绑定 → 门禁落 UserKey + 归一化持久化 → 缓存即时出数据 →
    // 统一走 refresher/bootstrap;网络失败按有无缓存分流。禁止在此复制三套刷新/缓存/引导逻辑。
    private Task<AuthResult> FinishLocalUnlockAsync(
        AuthenticationAttemptLease attempt,
        PersistedSession persisted,
        SymmetricCryptoKey userKey,
        CancellationToken ct,
        bool isPinUnlock = false)
    {
        var accountUserKey = VaultUserKey.TakeOwnership(userKey);
        return FinishLocalUnlockAsync(
            attempt,
            persisted,
            accountUserKey,
            persisted.AccountCryptographicState?.WrappedPrivateKey,
            ct,
            isPinUnlock);
    }

    private async Task<AuthResult> FinishLocalUnlockAsync(
        AuthenticationAttemptLease attempt,
        PersistedSession persisted,
        VaultUserKey userKey,
        string? wrappedPrivateKey,
        CancellationToken ct,
        bool isPinUnlock = false)
    {
        if (GetAccountDeletionUnlockBlock(persisted) is { } deletionBlock)
        {
            userKey.Dispose();
            AbandonAuthenticationAttempt(attempt);
            return deletionBlock;
        }
        if (!_session.TryRestorePersistedSession(attempt, persisted, out var lease))
        {
            userKey.Dispose();
            return AuthenticationExpired();
        }
        var normalized = persisted with { Generation = lease!.Generation };

        var transferred = false;
        var cacheGenerationCurrent = persisted.Generation == lease!.Generation;
        try
        {
            // Cache generation rebinding is an optimization. A persistence failure
            // makes the cache unavailable for this attempt but must not block refresh.
            if (!cacheGenerationCurrent)
                cacheGenerationCurrent = _cache.RebindGeneration(lease!);

            try
            {
                if (!_session.TryCommit(lease!, () =>
                {
                    if (_tokenStore.TryLoad(out var latest)
                        && latest.HasValidBinding
                        && latest.AccountId == lease.AccountId
                        && latest.Endpoints == lease.Endpoints)
                    {
                        normalized = normalized with
                        {
                            RefreshToken = latest.RefreshToken,
                            HasMasterPassword = latest.HasMasterPassword,
                        };
                    }
                    // Persist the rotated generation before transferring key and refresh-token ownership.
                    _tokenStore.Save(normalized);
                    _session.SetUnlockedKey(userKey);
                    _session.SetEncryptedPrivateKey(wrappedPrivateKey);
                    _session.SetRefreshTokenOnly(normalized.RefreshToken);
                    _deviceTrust?.SetCurrentSessionCapability(
                        lease,
                        normalized.SupportsDeviceTrust);
                    transferred = true;
                }))
                    return SessionExpiredFailure();
            }
            catch (Exception ex) when (IsExpectedPersistenceException(ex))
            {
                _session.TryLockIfCurrent(lease!);
                return SafeFailure("无法安全保存会话，请检查磁盘权限后重试。");
            }
        }
        finally
        {
            if (!transferred)
                userKey.Dispose();
        }

        if (!_session.TryCopyUserKeyMaterial(lease!, out var cacheKey))
            return SessionExpiredFailure();
        bool fromCache;
        using (cacheKey)
            fromCache = cacheGenerationCurrent
                && _cache.TryLoadInto(lease!, cacheKey!);

        if (isPinUnlock && IsRemovePinUnlockPolicyEnabled())
            return RevokePinUnlock(lease!);

        try
        {
            if (!await _refresher.TryRefreshAsync(lease!, ct).ConfigureAwait(false))
                return FinishUnlockOffline(lease!, fromCache);

            await _bootstrapper.BootstrapAsync(lease!, ct).ConfigureAwait(false);
            if (isPinUnlock && IsRemovePinUnlockPolicyEnabled())
                return RevokePinUnlock(lease!);
            return CompleteLocalUnlockSuccess(lease!);
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            // 刷新成功但整库同步失败(如账户错配 InvalidDataException/网络抖动)→ 按有无缓存分流。
            return FinishUnlockOffline(lease!, fromCache);
        }
    }

    // 网络刷新/整库同步失败后的收尾:有缓存 → 保持已解锁状态(DataStale 已由 TryLoadInto 置 true),
    // 离线解锁成功;无缓存 → 回滚(Lock 清零 UserKey)并失败。两条分支都在租约门禁内提交。
    private AuthResult FinishUnlockOffline(VaultSessionLease lease, bool fromCache)
    {
        if (GetAccountDeletionUnlockBlock(lease.AccountId, lease.Endpoints) is { } deletionBlock)
        {
            _session.TryLockIfCurrent(lease);
            return deletionBlock;
        }
        if (fromCache
            && _session.TryCommit(lease, () => _session.SetState(VaultState.Unlocked)))
            return new AuthResult.Success(lease);

        _session.TryLockIfCurrent(lease);
        return SafeFailure("无法连接服务器，且无离线缓存，请联网后重试。");
    }

    private AuthResult CompleteLocalUnlockSuccess(VaultSessionLease lease)
    {
        if (GetAccountDeletionUnlockBlock(lease.AccountId, lease.Endpoints) is { } deletionBlock)
        {
            _session.TryLockIfCurrent(lease);
            return deletionBlock;
        }
        return _session.IsUnlocked(lease)
            ? new AuthResult.Success(lease)
            : SessionExpiredFailure();
    }

    private AuthResult.Failure? GetAccountDeletionUnlockBlock(PersistedSession persisted) =>
        GetAccountDeletionUnlockBlock(persisted.AccountId, persisted.Endpoints!);

    private AuthResult.Failure? GetAccountDeletionUnlockBlock(
        string accountId,
        ServerEndpoints endpoints)
    {
        if (_accountDeletionTombstones is null)
            return null;

        try
        {
            var read = _accountDeletionTombstones.ReadAll();
            if (read.Status is AccountDeletionTombstoneReadStatus.Corrupt
                or AccountDeletionTombstoneReadStatus.Unavailable)
            {
                return new AuthResult.Failure(
                    "Account deletion recovery state is unavailable. Local unlock is blocked.",
                    AuthFailureKind.AccountDeletionRecoveryRequired);
            }
            if (read.Status == AccountDeletionTombstoneReadStatus.Found
                && read.Records.Any(record =>
                    record.Binding.AccountId == accountId
                    && record.Binding.Endpoints == endpoints))
            {
                return new AuthResult.Failure(
                    "Account deletion recovery must be resolved before local unlock.",
                    AuthFailureKind.AccountDeletionRecoveryRequired);
            }
            return null;
        }
        catch
        {
            return new AuthResult.Failure(
                "Account deletion recovery state is unavailable. Local unlock is blocked.",
                AuthFailureKind.AccountDeletionRecoveryRequired);
        }
    }

    private static AuthResult.Failure AccountDeletionCoordinationUnavailable() => new(
        "Account deletion coordination is unavailable. Authentication is blocked.",
        AuthFailureKind.AccountDeletionRecoveryRequired);

    public async Task<AuthResult> CompleteRequiredPasswordChangeAsync(
        VaultSessionLease restrictedSession,
        string newPassword,
        string? hint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(restrictedSession);
        if (!TryGetPendingRequiredPasswordChange(
                restrictedSession,
                out var pending,
                out var operation))
            return SessionExpiredFailure();
        using var operationScope = operation;

        if (string.IsNullOrEmpty(newPassword)
            || newPassword.Length < MasterPasswordPolicyRequirements.MinimumLength
            || newPassword.Length > MasterPasswordPolicyRequirements.MaximumLength)
        {
            return SafeFailure(
                $"The new master password must contain between "
                + $"{MasterPasswordPolicyRequirements.MinimumLength} and "
                + $"{MasterPasswordPolicyRequirements.MaximumLength} characters.");
        }

        var normalizedHint = string.IsNullOrWhiteSpace(hint) ? null : hint.Trim();
        if (normalizedHint is { Length: > 50 }
            || string.Equals(normalizedHint, newPassword, StringComparison.Ordinal))
        {
            return SafeFailure(
                "The password hint must be at most 50 characters and cannot equal the password.");
        }

        var evidence = _passwordPolicyEvaluator.CaptureEvidence(newPassword);
        if (!_passwordPolicyEvaluator.Evaluate(pending.Policy, evidence))
            return SafeFailure("The new master password does not satisfy the organization policy.");

        IAccountDeletionCoordinationLease coordination;
        try
        {
            coordination = await _accountDeletionCoordinationGate
                .AcquireAsync(ct)
                .ConfigureAwait(false);
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
        if (GetAccountDeletionUnlockBlock(
                restrictedSession.AccountId,
                restrictedSession.Endpoints) is { } deletionBlock)
        {
            EndRequiredPasswordChange(pending);
            return deletionBlock;
        }

        byte[]? newMasterKey = null;
        string newPasswordHash;
        string wrappedUserKey;
        try
        {
            newMasterKey = _crypto.DeriveMasterKey(
                newPassword,
                pending.MasterPasswordSalt,
                pending.KdfType,
                pending.KdfIterations,
                pending.KdfMemory,
                pending.KdfParallelism);
            newPasswordHash = _crypto.ComputeMasterPasswordHash(
                newMasterKey,
                newPassword);
            wrappedUserKey = _crypto.ProtectUserKey(
                newMasterKey,
                pending.UserKey).ToString();
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return InvalidResponseFailure();
        }
        finally
        {
            if (newMasterKey is not null)
                CryptographicOperations.ZeroMemory(newMasterKey);
        }

        var kdf = new KdfParams(
            (int)pending.KdfType,
            pending.KdfIterations,
            pending.KdfMemory,
            pending.KdfParallelism);
        var authenticationData = new AuthData(
            pending.MasterPasswordSalt,
            kdf,
            newPasswordHash);
        var unlockData = new UnlockData(
            pending.MasterPasswordSalt,
            kdf,
            wrappedUserKey);

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, restrictedSession.CancellationToken);
            var api = _clients.GetAuthenticated<IAccountApiClient>(restrictedSession);
            if (pending.Reason == RequiredPasswordChangeReason.AdministratorTemporaryPassword)
            {
                await api.UpdateTemporaryPasswordAsync(
                    new UpdateTemporaryPasswordRequest(
                        authenticationData,
                        unlockData,
                        normalizedHint),
                    linked.Token).ConfigureAwait(false);
            }
            else
            {
                await api.ChangePasswordAsync(
                    new ChangePasswordRequest(
                        pending.CurrentPasswordHash,
                        newPasswordHash,
                        normalizedHint,
                        wrappedUserKey,
                        authenticationData,
                        unlockData),
                    linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            EndRequiredPasswordChange(pending);
            return new AuthResult.PasswordChangeUncertain();
        }
        catch (VaultWriteException exception) when (
            exception.StatusCode is System.Net.HttpStatusCode.BadRequest
                or System.Net.HttpStatusCode.UnprocessableEntity)
        {
            return SafeFailure("The server rejected the new master password.");
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            EndRequiredPasswordChange(pending);
            return new AuthResult.PasswordChangeUncertain();
        }

        EndRequiredPasswordChange(pending);
        return new AuthResult.PasswordChanged();
    }

    private void EndRequiredPasswordChange(PendingRequiredPasswordChangeContext pending)
    {
        lock (_pendingGate)
        {
            if (ReferenceEquals(_pendingRequiredPasswordChange, pending))
                _pendingRequiredPasswordChange = null;
        }
        _session.TryCommit(pending.Lease, _session.Logout);
        pending.Dispose();
    }

    // core 原语:第一动作即失效旧 lease(rotation-first);不停止通知、不清剪贴板、不导航——
    // 第三层 App 生命周期协调器必须在外层阻止新敏感操作并完成 App 编排。
    public Task LockAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            LockCurrentSession();
        }
        finally
        {
            DisposePendingTwoFactor();
            DisposePendingLoginWithDevice();
            DisposePendingRequiredPasswordChange(endRestrictedSession: false);
            ClearPendingTrustedDeviceAccount();
        }
        return Task.CompletedTask;
    }

    public Task LockAsync(
        VaultSessionLease expectedSession,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedSession);
        ct.ThrowIfCancellationRequested();
        var lockStarted = false;
        try
        {
            _session.TryCommit(expectedSession, () =>
            {
                lockStarted = true;
                LockCurrentSession();
            });
        }
        finally
        {
            if (lockStarted)
            {
                DisposePendingTwoFactor();
                DisposePendingLoginWithDevice();
                DisposePendingRequiredPasswordChange(endRestrictedSession: false);
                ClearPendingTrustedDeviceAccount();
            }
        }
        return Task.CompletedTask;
    }

    private void LockCurrentSession()
    {
        var removePinUnlock = IsRemovePinUnlockPolicyEnabled();
        var lockedLease = _session.Lock();
        if (lockedLease is not null)
        {
            _session.TryCommit(lockedLease, () =>
            {
                if (_tokenStore.TryLoad(out var persisted)
                    && persisted.AccountId == lockedLease.AccountId
                    && persisted.Endpoints == lockedLease.Endpoints)
                    _tokenStore.Save(persisted with
                    {
                        Generation = lockedLease.Generation,
                        PinProtectedUserKey = removePinUnlock
                            ? null
                            : persisted.PinProtectedUserKey,
                        PinFailedAttempts = removePinUnlock
                            ? 0
                            : persisted.PinFailedAttempts,
                    });
                _cache.RebindGeneration(lockedLease);
            });
        }
    }

    public Task LogoutAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            LogoutCurrentSession();
        }
        finally
        {
            DisposePendingTwoFactor();
            DisposePendingLoginWithDevice();
            DisposePendingRequiredPasswordChange(endRestrictedSession: false);
            ClearPendingTrustedDeviceAccount();
        }
        return Task.CompletedTask;
    }

    public Task<bool> LogoutAsync(
        VaultSessionLease expectedSession,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedSession);
        ct.ThrowIfCancellationRequested();
        var claimed = false;
        try
        {
            var committed = _session.TryCommit(expectedSession, () =>
            {
                claimed = true;
                LogoutCurrentSession();
            });
            return Task.FromResult(committed);
        }
        finally
        {
            if (claimed)
            {
                DisposePendingTwoFactor();
                DisposePendingLoginWithDevice();
                DisposePendingRequiredPasswordChange(endRestrictedSession: false);
                ClearPendingTrustedDeviceAccount();
            }
        }
    }

    private void LogoutCurrentSession()
    {
        var account = _session.Account;
        _session.Lock();
        Exception? persistenceFailure = null;
        if (!string.IsNullOrWhiteSpace(account.Id) && account.Endpoints is { } endpoints)
        {
            try
            {
                if (!_tokenStore.Remove(account.Id, endpoints))
                    persistenceFailure = new InvalidDataException(
                        "The saved session was not removed.");
            }
            catch (Exception ex) when (IsExpectedPersistenceException(ex))
            {
                persistenceFailure = ex;
            }

            try
            {
                _cache.Clear(account.Id, endpoints);
            }
            catch (Exception ex) when (IsExpectedPersistenceException(ex))
            {
                persistenceFailure ??= ex;
            }
        }
        else
        {
            try
            {
                _tokenStore.Clear();
            }
            catch (Exception ex) when (IsExpectedPersistenceException(ex))
            {
                persistenceFailure = ex;
            }

            try
            {
                _cache.Clear();
            }
            catch (Exception ex) when (IsExpectedPersistenceException(ex))
            {
                persistenceFailure ??= ex;
            }
        }

        if (persistenceFailure is not null)
            throw new AuthSessionPersistenceException(
                "The saved session could not be removed securely. Retry logout after checking storage access.");

        _session.Logout();
    }

    private void DisposePendingLoginWithDevice()
    {
        PendingLoginWithDeviceContext? pending;
        PendingLoginWithDeviceContext? active;
        PendingLoginWithDeviceTwoFactorContext? twoFactor;
        lock (_pendingGate)
        {
            pending = _pendingLoginWithDevice;
            active = _activeLoginWithDeviceExchange;
            twoFactor = _pendingLoginWithDeviceTwoFactor;
            _pendingLoginWithDevice = null;
            _activeLoginWithDeviceExchange = null;
            _pendingLoginWithDeviceTwoFactor = null;
            pending?.Dispose();
            active?.Dispose();
            twoFactor?.Dispose();
        }

        var attempt = pending?.Binding.Attempt
            ?? active?.Binding.Attempt
            ?? twoFactor?.Binding.Attempt;
        if (attempt is not null)
            _loginWithDevice?.Cancel(attempt);
        var operationContext = (PendingLoginWithDeviceOperationContext?)active
            ?? twoFactor;
        if (operationContext is not null)
            _ = RevokePromotedLoginWithDeviceSession(operationContext);
    }

    private void DisposePendingLoginWithDevice(
        AuthenticationAttemptLease attempt,
        bool removeQuarantineWhenNoIdentityExchangeIsInFlight = false)
    {
        PendingLoginWithDeviceContext? pending = null;
        PendingLoginWithDeviceContext? active = null;
        PendingLoginWithDeviceTwoFactorContext? twoFactor = null;
        lock (_pendingGate)
        {
            if (_pendingLoginWithDevice?.Binding.Attempt == attempt)
            {
                pending = _pendingLoginWithDevice;
                _pendingLoginWithDevice = null;
                pending.Dispose();
            }
            if (_activeLoginWithDeviceExchange?.Binding.Attempt == attempt)
            {
                active = _activeLoginWithDeviceExchange;
                _activeLoginWithDeviceExchange = null;
                active.Dispose();
            }
            if (_pendingLoginWithDeviceTwoFactor?.Binding.Attempt == attempt)
            {
                twoFactor = _pendingLoginWithDeviceTwoFactor;
                _pendingLoginWithDeviceTwoFactor = null;
                twoFactor.Dispose();
            }
        }

        _loginWithDevice?.Cancel(attempt);
        var operationContext = (PendingLoginWithDeviceOperationContext?)active
            ?? twoFactor;
        var revocationSucceeded = operationContext is null
            || RevokePromotedLoginWithDeviceSession(operationContext);
        var identityExchangeStarted = active?.IdentityExchangeStarted == true
            || twoFactor?.IdentityExchangeStarted == true;
        var quarantineRecord = pending?.QuarantineRecord
            ?? active?.QuarantineRecord
            ?? twoFactor?.QuarantineRecord;
        if (removeQuarantineWhenNoIdentityExchangeIsInFlight
            && revocationSucceeded
            && !identityExchangeStarted
            && quarantineRecord is not null)
        {
            _ = TryRemoveLoginWithDeviceQuarantine(
                quarantineRecord,
                LoginWithDeviceQuarantineRemovalReason.DefiniteOutcome);
        }
    }

    private bool TryAcquirePendingLoginWithDevice(
        AuthenticationAttemptLease attempt,
        out PendingLoginWithDeviceContext context,
        out IDisposable operation)
    {
        lock (_pendingGate)
        {
            if (_pendingLoginWithDevice is { } pending
                && pending.Binding.Attempt == attempt
                && pending.TryAcquireOperation(out operation))
            {
                context = pending;
                return true;
            }
        }

        context = null!;
        operation = null!;
        return false;
    }

    private bool TryGetPendingLoginWithDevice(
        AuthenticationAttemptLease attempt,
        out PendingLoginWithDeviceContext context)
    {
        lock (_pendingGate)
        {
            if (_pendingLoginWithDevice is { } pending
                && pending.Binding.Attempt == attempt
                && !pending.DisposeRequested)
            {
                context = pending;
                return true;
            }
        }

        context = null!;
        return false;
    }

    private bool TryGetActiveLoginWithDeviceExchange(
        AuthenticationAttemptLease attempt,
        out PendingLoginWithDeviceContext context)
    {
        lock (_pendingGate)
        {
            if (_activeLoginWithDeviceExchange is { } active
                && active.Binding.Attempt == attempt
                && !active.DisposeRequested)
            {
                context = active;
                return true;
            }
        }

        context = null!;
        return false;
    }

    private bool TryAcquirePendingLoginWithDeviceTwoFactor(
        AuthenticationAttemptLease attempt,
        out PendingLoginWithDeviceTwoFactorContext context,
        out IDisposable operation)
    {
        lock (_pendingGate)
        {
            if (_pendingLoginWithDeviceTwoFactor is { } pending
                && pending.Binding.Attempt == attempt
                && pending.TryAcquireOperation(out operation))
            {
                context = pending;
                return true;
            }
        }

        context = null!;
        operation = null!;
        return false;
    }

    private bool TryClaimApprovedLoginWithDevice(
        PendingLoginWithDeviceContext pending)
    {
        lock (_pendingGate)
        {
            if (!ReferenceEquals(_pendingLoginWithDevice, pending))
                return false;
            _pendingLoginWithDevice = null;
            _activeLoginWithDeviceExchange = pending;
            return true;
        }
    }

    private void CompleteActiveLoginWithDeviceExchange(
        PendingLoginWithDeviceContext pending)
    {
        lock (_pendingGate)
        {
            if (ReferenceEquals(_activeLoginWithDeviceExchange, pending))
                _activeLoginWithDeviceExchange = null;
            pending.Dispose();
        }
    }

    private void CompleteLoginWithDeviceRequest(
        PendingLoginWithDeviceContext pending,
        bool cancelService)
    {
        lock (_pendingGate)
        {
            if (ReferenceEquals(_pendingLoginWithDevice, pending))
                _pendingLoginWithDevice = null;
        }
        if (cancelService)
            _loginWithDevice?.Cancel(pending.Binding.Attempt);
        pending.Dispose();
    }

    private void DetachPendingLoginWithDeviceTwoFactor(
        PendingLoginWithDeviceTwoFactorContext pending)
    {
        lock (_pendingGate)
        {
            if (ReferenceEquals(_pendingLoginWithDeviceTwoFactor, pending))
                _pendingLoginWithDeviceTwoFactor = null;
        }
        pending.Dispose();
    }

    private void ExpireLoginWithDeviceTwoFactor(
        PendingLoginWithDeviceTwoFactorContext pending)
    {
        if (!pending.TryRequestExpiry(
                _timeProvider.GetUtcNow(),
                pending.Binding.ExpiresAtUtc))
        {
            return;
        }

        var detached = false;
        lock (_pendingGate)
        {
            if (ReferenceEquals(_pendingLoginWithDeviceTwoFactor, pending))
            {
                _pendingLoginWithDeviceTwoFactor = null;
                detached = true;
            }
        }

        if (!detached)
            return;
        pending.Dispose();
        var identityExchangeStarted = pending.IdentityExchangeStarted;
        _session.AbandonAuthenticationAttempt(pending.Binding.Attempt);
        _loginWithDevice?.Cancel(pending.Binding.Attempt);
        var revocationSucceeded = RevokePromotedLoginWithDeviceSession(pending);
        if (revocationSucceeded
            && !identityExchangeStarted
            && pending.QuarantineRecord is { } quarantineRecord)
        {
            _ = TryRemoveLoginWithDeviceQuarantine(
                quarantineRecord,
                LoginWithDeviceQuarantineRemovalReason.DefiniteOutcome);
        }
    }

    private bool RevokePromotedLoginWithDeviceSession(
        PendingLoginWithDeviceOperationContext context)
    {
        lock (_authenticationTransitionGate)
        {
            return context.RevokePromotedSession(lease =>
            {
                // Keep lease revocation and exact persistence cleanup in one
                // authentication transition. A new same-binding login cannot
                // persist its session between these two operations.
                if (!_session.TryLockIfCurrent(lease))
                    return true;
                return TryRemovePersistedAccountData(
                    lease.AccountId,
                    lease.Endpoints);
            });
        }
    }

    private void ClearPendingTrustedDeviceAccount()
    {
        lock (_pendingGate)
            _pendingTrustedDeviceAccount = null;
    }

    private void ClearPendingTrustedDeviceAccount(VaultSessionLease expectedSession)
    {
        lock (_pendingGate)
        {
            if (_pendingTrustedDeviceAccount is { } pending
                && pending.Lease.SameSessionAs(expectedSession))
            {
                _pendingTrustedDeviceAccount = null;
            }
        }
    }

    private LoginWithDeviceQuarantineRecord? TryArmLoginWithDeviceQuarantine(
        LoginWithDeviceBinding binding)
    {
        if (_loginWithDeviceQuarantineStore is null)
            return null;

        try
        {
            var armedAt = _timeProvider.GetUtcNow();
            var clearEligibleAt = binding.ExpiresAtUtc
                <= DateTimeOffset.MaxValue - LoginWithDeviceQuarantineClearDelay
                    ? binding.ExpiresAtUtc + LoginWithDeviceQuarantineClearDelay
                    : DateTimeOffset.MaxValue;
            return _loginWithDeviceQuarantineStore.TryArm(
                binding.QuarantineBinding,
                binding.RequestId,
                armedAt,
                binding.ExpiresAtUtc,
                clearEligibleAt);
        }
        catch (Exception ex) when (
            IsExpectedPersistenceException(ex) || IsExpectedAuthException(ex))
        {
            return null;
        }
    }

    private bool TryRemoveLoginWithDeviceQuarantine(
        LoginWithDeviceQuarantineRecord record,
        LoginWithDeviceQuarantineRemovalReason reason)
    {
        if (_loginWithDeviceQuarantineStore is null)
            return false;

        try
        {
            if (_loginWithDeviceQuarantineStore.Remove(record, reason))
                return true;
            return _loginWithDeviceQuarantineStore.Read(record.Binding).Status
                == LoginWithDeviceQuarantineStoreReadStatus.Absent;
        }
        catch (Exception ex) when (
            IsExpectedPersistenceException(ex) || IsExpectedAuthException(ex))
        {
            return false;
        }
    }

    private void TryClearMatchingQuarantineAfterSessionPersisted(
        ServerEndpoints endpoints,
        string? authenticatedEmail,
        string deviceIdentifier)
    {
        if (_loginWithDeviceQuarantineStore is null
            || string.IsNullOrWhiteSpace(authenticatedEmail))
        {
            return;
        }

        try
        {
            var binding = new LoginWithDeviceQuarantineBinding(
                endpoints,
                authenticatedEmail,
                deviceIdentifier);
            var read = _loginWithDeviceQuarantineStore.Read(binding);
            if (read is
                {
                    Status: LoginWithDeviceQuarantineStoreReadStatus.Found,
                    Record: { } record,
                })
            {
                _ = _loginWithDeviceQuarantineStore.Remove(
                    record,
                    LoginWithDeviceQuarantineRemovalReason.SessionPersisted);
            }
        }
        catch (Exception ex) when (
            IsExpectedPersistenceException(ex) || IsExpectedAuthException(ex))
        {
        }
    }

    private LoginWithDeviceQuarantineInfo ToQuarantineInfo(
        LoginWithDeviceQuarantineStoreReadResult read) => read switch
    {
        {
            Status: LoginWithDeviceQuarantineStoreReadStatus.Found,
            Record: { } record,
        } => new LoginWithDeviceQuarantineInfo.Found(
            record.EntryId,
            record.Revision,
            record.RequestId,
            record.ArmedAt,
            record.RequestExpiresAt,
            record.ClearEligibleAt,
            record.IsClearEligible(_timeProvider.GetUtcNow())),
        { Status: LoginWithDeviceQuarantineStoreReadStatus.Absent } =>
            new LoginWithDeviceQuarantineInfo.Absent(),
        { Status: LoginWithDeviceQuarantineStoreReadStatus.Corrupt } =>
            new LoginWithDeviceQuarantineInfo.Corrupt(),
        _ => new LoginWithDeviceQuarantineInfo.Unavailable(),
    };

    private static LoginWithDeviceQuarantineClearStatus? ClassifyQuarantineForClear(
        LoginWithDeviceQuarantineStoreReadResult read,
        LoginWithDeviceQuarantineInfo.Found expected,
        out LoginWithDeviceQuarantineRecord? record)
    {
        record = read.Record;
        switch (read.Status)
        {
            case LoginWithDeviceQuarantineStoreReadStatus.Absent:
                return LoginWithDeviceQuarantineClearStatus.AlreadyAbsent;
            case LoginWithDeviceQuarantineStoreReadStatus.Corrupt:
                return LoginWithDeviceQuarantineClearStatus.Corrupt;
            case LoginWithDeviceQuarantineStoreReadStatus.Unavailable:
                return LoginWithDeviceQuarantineClearStatus.Unavailable;
            case LoginWithDeviceQuarantineStoreReadStatus.Found
                when record is null:
                return LoginWithDeviceQuarantineClearStatus.Corrupt;
            case LoginWithDeviceQuarantineStoreReadStatus.Found:
                return record!.EntryId == expected.EntryId
                    && record.Revision == expected.Revision
                    && string.Equals(
                        record.RequestId,
                        expected.RequestId,
                        StringComparison.Ordinal)
                    && record.ArmedAt == expected.ArmedAtUtc
                    && record.RequestExpiresAt == expected.RequestExpiresAtUtc
                    && record.ClearEligibleAt == expected.ClearEligibleAtUtc
                        ? null
                        : LoginWithDeviceQuarantineClearStatus.Changed;
            default:
                return LoginWithDeviceQuarantineClearStatus.Unavailable;
        }
    }

    private static AuthResult.LoginWithDeviceWaiting ToWaitingResult(
        PendingLoginWithDeviceContext pending) => new(
            pending.Binding.RequestId,
            pending.Binding.FingerprintPhrase,
            pending.Binding.ExpiresAtUtc);

    private static AuthResult.LoginWithDevicePending ToPendingResult(
        PendingLoginWithDeviceContext pending,
        LoginWithDeviceUiStatus status) => new(
            pending.Binding.RequestId,
            pending.Binding.FingerprintPhrase,
            pending.Binding.ExpiresAtUtc,
            status);

    private static void ValidateLoginWithDeviceGrant(
        LoginWithDeviceBinding binding,
        LoginWithDeviceGrant grant)
    {
        if (!string.Equals(
                grant.RequestId,
                binding.RequestId,
                StringComparison.Ordinal)
            || !string.Equals(grant.Email, binding.Email, StringComparison.Ordinal)
            || grant.Endpoints != binding.Endpoints
            || !string.Equals(
                grant.DeviceIdentifier,
                binding.DeviceIdentifier,
                StringComparison.Ordinal)
            || grant.UserKeySecurityVersion is not (1 or 2))
        {
            throw new InvalidDataException(
                "The approved device-login grant does not match the pending request.");
        }
    }

    private static ConnectTokenRequest CreateLoginWithDeviceTokenRequest(
        LoginWithDeviceBinding binding,
        LoginWithDeviceGrant grant,
        TwoFactorPayload? twoFactor) =>
        ConnectTokenRequest.AuthRequest(
            binding.Email,
            new string(grant.AccessCode.Span),
            binding.RequestId,
            binding.DeviceIdentifier,
            DeviceName,
            twoFactor);

    private static SendEmailLoginRequest CreateLoginWithDeviceEmailRequest(
        PendingLoginWithDeviceTwoFactorContext context) => new(
            context.Binding.Email,
            null,
            context.Binding.DeviceIdentifier,
            SsoEmail2FaSessionToken: null,
            AuthRequestId: context.Binding.RequestId,
            AuthRequestAccessCode: new string(context.Grant.AccessCode.Span));

    private string? ResolveLoginWithDeviceRememberToken(
        LoginWithDeviceBinding binding,
        TokenResponse token)
    {
        if (!string.IsNullOrEmpty(token.TwoFactorToken))
            return token.TwoFactorToken;
        return TryLoadLoginWithDeviceRememberToken(binding);
    }

    private TwoFactorPayload? TryLoadLoginWithDeviceRememberPayload(
        LoginWithDeviceBinding binding)
    {
        var token = TryLoadLoginWithDeviceRememberToken(binding);
        return string.IsNullOrEmpty(token)
            ? null
            : new TwoFactorPayload(RememberTwoFactorProvider, token, false);
    }

    private string? TryLoadLoginWithDeviceRememberToken(
        LoginWithDeviceBinding binding)
    {
        try
        {
            return _tokenStore.TryLoad(out var prior)
                && HasMatchingLoginWithDeviceBinding(prior, binding)
                && !string.IsNullOrEmpty(prior.TwoFactorToken)
                ? prior.TwoFactorToken
                : null;
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            return null;
        }
    }

    private static bool HasMatchingLoginWithDeviceBinding(
        PersistedSession persisted,
        LoginWithDeviceBinding binding)
    {
        return persisted.Endpoints == binding.Endpoints
            && string.Equals(
                MasterPasswordSaltPolicy.NormalizeEmail(persisted.Email),
                binding.Email,
                StringComparison.Ordinal)
            && Guid.TryParseExact(
                persisted.DeviceIdentifier,
                "D",
                out var persistedDeviceIdentifier)
            && Guid.TryParseExact(
                binding.DeviceIdentifier,
                "D",
                out var bindingDeviceIdentifier)
            && bindingDeviceIdentifier != Guid.Empty
            && persistedDeviceIdentifier == bindingDeviceIdentifier
            && string.Equals(
                bindingDeviceIdentifier.ToString("D"),
                binding.DeviceIdentifier,
                StringComparison.Ordinal);
    }

    private void DisposePendingTwoFactor()
    {
        PendingTwoFactorContext? pending;
        ITimer? timer;
        lock (_pendingGate)
        {
            pending = _pendingTwoFactor;
            _pendingTwoFactor = null;
            _pendingSsoTwoFactor = null;
            timer = _deviceVerificationExpiryTimer;
            _deviceVerificationExpiryTimer = null;
        }
        timer?.Dispose();
        DisposePendingPasswordSecrets(pending);
    }

    private void DisposePendingTwoFactor(AuthenticationAttemptLease attempt)
    {
        PendingTwoFactorContext? pending = null;
        ITimer? timer = null;
        lock (_pendingGate)
        {
            if (_pendingTwoFactor?.Attempt == attempt)
            {
                pending = _pendingTwoFactor;
                _pendingTwoFactor = null;
                timer = _deviceVerificationExpiryTimer;
                _deviceVerificationExpiryTimer = null;
            }
            if (_pendingSsoTwoFactor?.Attempt == attempt)
                _pendingSsoTwoFactor = null;
        }
        timer?.Dispose();
        DisposePendingPasswordSecrets(pending);
    }

    private static void DisposePendingPasswordSecrets(
        PendingTwoFactorContext? context)
    {
        if (context is null)
            return;
        context.StretchedKey.Dispose();
        Array.Clear(context.MasterPassword);
    }

    private void ExpireDeviceVerification(PendingTwoFactorContext expected)
    {
        PendingTwoFactorContext? expired = null;
        ITimer? timer = null;
        lock (_pendingGate)
        {
            if (ReferenceEquals(_pendingTwoFactor, expected)
                && expected.RequiresDeviceVerification)
            {
                expired = _pendingTwoFactor;
                _pendingTwoFactor = null;
                timer = _deviceVerificationExpiryTimer;
                _deviceVerificationExpiryTimer = null;
            }
        }

        if (expired is null)
            return;
        _session.AbandonAuthenticationAttempt(expired.Attempt);
        DisposePendingPasswordSecrets(expired);
        timer?.Dispose();
    }

    private void DisposePendingRequiredPasswordChange(bool endRestrictedSession)
    {
        PendingRequiredPasswordChangeContext? pending;
        lock (_pendingGate)
        {
            pending = _pendingRequiredPasswordChange;
            _pendingRequiredPasswordChange = null;
        }

        if (pending is null)
            return;
        if (endRestrictedSession)
            _session.TryCommit(pending.Lease, _session.Logout);
        pending.Dispose();
    }

    private bool TryGetPendingRequiredPasswordChange(
        VaultSessionLease lease,
        out PendingRequiredPasswordChangeContext context,
        out IDisposable operation)
    {
        PendingRequiredPasswordChangeContext? candidate;
        lock (_pendingGate)
        {
            candidate = _pendingRequiredPasswordChange;
        }

        if (candidate is not null
            && candidate.Lease.SameSessionAs(lease)
            && _session.IsCurrent(lease)
            && candidate.TryAcquireOperation(out operation))
        {
            context = candidate;
            return true;
        }

        context = null!;
        operation = null!;
        return false;
    }

    private AuthenticationAttemptLease? GetPendingTwoFactorAttempt()
    {
        lock (_pendingGate)
            return _pendingLoginWithDeviceTwoFactor?.Binding.Attempt
                ?? _pendingTwoFactor?.Attempt
                ?? _pendingSsoTwoFactor?.Attempt;
    }

    private bool TryGetPendingTwoFactor(
        AuthenticationAttemptLease attempt,
        out PendingTwoFactorContext context)
    {
        lock (_pendingGate)
        {
            if (_pendingTwoFactor is { } pending && pending.Attempt == attempt)
            {
                context = pending;
                return true;
            }
        }
        context = null!;
        return false;
    }

    private bool TryGetPendingSsoTwoFactor(
        AuthenticationAttemptLease attempt,
        out PendingSsoTwoFactorContext context)
    {
        lock (_pendingGate)
        {
            if (_pendingSsoTwoFactor is { } pending && pending.Attempt == attempt)
            {
                context = pending;
                return true;
            }
        }
        context = null!;
        return false;
    }

    private async Task<AuthResult> HandleLoginResultAsync(
        PendingTwoFactorContext context,
        ConnectTokenResult result,
        CancellationToken ct,
        bool autoSendEmail = false)
    {
        switch (result)
        {
            case ConnectTokenResult.Success success:
                ITimer? successTimer = null;
                lock (_pendingGate)
                {
                    if (_pendingTwoFactor?.Attempt == context.Attempt)
                    {
                        _pendingTwoFactor = null;
                        successTimer = _deviceVerificationExpiryTimer;
                        _deviceVerificationExpiryTimer = null;
                    }
                }
                successTimer?.Dispose();
                return await CompleteLoginAsync(context, success.Token, ct);
            case ConnectTokenResult.TwoFactorRequired twoFactor:
                var providerData = NormalizeProviderData(twoFactor.ProviderData);
                var pending = context with
                {
                    Providers = twoFactor.Providers.ToArray(),
                    ProviderData = providerData,
                };
                PendingTwoFactorContext? stale;
                ITimer? staleTimer;
                lock (_pendingGate)
                {
                    stale = _pendingTwoFactor;
                    _pendingTwoFactor = pending;
                    staleTimer = _deviceVerificationExpiryTimer;
                    _deviceVerificationExpiryTimer = null;
                }
                staleTimer?.Dispose();
                if (stale is not null
                    && !ReferenceEquals(stale.StretchedKey, context.StretchedKey))
                    DisposePendingPasswordSecrets(stale);
                // 仅在首次进入验证码阶段(LoginAsync 路径)自动发码;2FA 提交失败再次挑战时不重复发信(避免每次错码都发一封)。
                if (autoSendEmail && twoFactor.Providers.Contains(EmailTwoFactorProvider))
                    await TrySendTwoFactorEmailAsync(pending, ct);
                if (ct.IsCancellationRequested
                    || !_session.IsCurrent(context.Attempt))
                {
                    AbandonAuthenticationAttempt(context.Attempt);
                    return AuthenticationExpired();
                }
                return new AuthResult.TwoFactorRequired(twoFactor.Providers, providerData);
            case ConnectTokenResult.DeviceVerificationRequired:
                var verificationPending = context with
                {
                    Providers = Array.Empty<int>(),
                    ProviderData = new Dictionary<int, string>(),
                    RequiresDeviceVerification = true,
                    DeviceVerificationExpiresAt = context.DeviceVerificationExpiresAt
                        ?? _timeProvider.GetUtcNow() + _newDeviceVerificationLifetime,
                };
                var expiresIn = verificationPending.DeviceVerificationExpiresAt!.Value
                    - _timeProvider.GetUtcNow();
                var verificationTimer = _timeProvider.CreateTimer(
                    static state =>
                    {
                        var (service, pendingContext) =
                            ((AuthService, PendingTwoFactorContext))state!;
                        service.ExpireDeviceVerification(pendingContext);
                    },
                    (this, verificationPending),
                    expiresIn > TimeSpan.Zero ? expiresIn : TimeSpan.Zero,
                    Timeout.InfiniteTimeSpan);
                PendingTwoFactorContext? prior;
                ITimer? priorTimer;
                lock (_pendingGate)
                {
                    prior = _pendingTwoFactor;
                    _pendingTwoFactor = verificationPending;
                    priorTimer = _deviceVerificationExpiryTimer;
                    _deviceVerificationExpiryTimer = verificationTimer;
                }
                priorTimer?.Dispose();
                if (prior is not null
                    && !ReferenceEquals(prior.StretchedKey, context.StretchedKey))
                {
                    DisposePendingPasswordSecrets(prior);
                }
                if (ct.IsCancellationRequested || !_session.IsCurrent(context.Attempt))
                {
                    AbandonAuthenticationAttempt(context.Attempt);
                    return AuthenticationExpired();
                }
                return new AuthResult.DeviceVerificationRequired();
            default:
                if (!TryGetPendingTwoFactor(context.Attempt, out _))
                {
                    DisposePendingPasswordSecrets(context);
                    _session.AbandonAuthenticationAttempt(context.Attempt);
                }
                return ToFailure(result);
        }
    }

    private async Task<AuthResult> HandleSsoLoginResultAsync(
        PendingSsoTwoFactorContext context,
        ConnectTokenResult result,
        CancellationToken ct,
        bool autoSendEmail = false)
    {
        switch (result)
        {
            case ConnectTokenResult.Success success:
                lock (_pendingGate)
                {
                    if (_pendingSsoTwoFactor?.Attempt == context.Attempt)
                        _pendingSsoTwoFactor = null;
                }
                return await CompleteSsoAuthenticationAsync(context, success.Token, ct);
            case ConnectTokenResult.TwoFactorRequired twoFactor:
                var pending = context with
                {
                    Providers = twoFactor.Providers.ToArray(),
                    ProviderData = NormalizeProviderData(twoFactor.ProviderData),
                    SsoEmail2FaSessionToken = twoFactor.SsoEmail2FaSessionToken,
                    ChallengeEmail = twoFactor.Email,
                };
                lock (_pendingGate)
                    _pendingSsoTwoFactor = pending;

                if (autoSendEmail && twoFactor.Providers.Contains(EmailTwoFactorProvider))
                    await TrySendSsoTwoFactorEmailAsync(pending, ct);
                if (ct.IsCancellationRequested || !_session.IsCurrent(context.Attempt))
                {
                    AbandonAuthenticationAttempt(context.Attempt);
                    return AuthenticationExpired();
                }
                return new AuthResult.TwoFactorRequired(
                    twoFactor.Providers,
                    pending.ProviderData);
            default:
                if (!TryGetPendingSsoTwoFactor(context.Attempt, out _))
                    _session.AbandonAuthenticationAttempt(context.Attempt);
                return ToFailure(result);
        }
    }

    private async Task TrySendSsoTwoFactorEmailAsync(
        PendingSsoTwoFactorContext context,
        CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, context.Attempt.CancellationToken);
            await Identity(context.Endpoints).SendTwoFactorEmailLoginAsync(
                CreateSsoEmailRequest(context),
                linked.Token);
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
        }
    }

    // 登录期发码:best-effort。失败(服务端未启用邮箱2FA / 网络抖动)不阻断进入验证码阶段,用户可点"重发"重试。
    private async Task TrySendTwoFactorEmailAsync(PendingTwoFactorContext context, CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, context.Attempt.CancellationToken);
            await Identity(context.Endpoints).SendTwoFactorEmailLoginAsync(
                new SendEmailLoginRequest(
                    context.Email,
                    context.PasswordHash,
                    context.DeviceIdentifier),
                linked.Token);
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
        }
    }

    public Task<bool> ResendTwoFactorEmailAsync(CancellationToken ct = default)
    {
        var attempt = GetPendingTwoFactorAttempt();
        return attempt is null
            ? Task.FromResult(false)
            : ResendTwoFactorEmailAsync(attempt, ct);
    }

    public async Task<bool> ResendTwoFactorEmailAsync(
        AuthenticationAttemptLease attempt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (TryAcquirePendingLoginWithDeviceTwoFactor(
                attempt,
                out var loginWithDeviceContext,
                out var loginWithDeviceOperation))
        {
            using (loginWithDeviceOperation)
            {
                if (!_session.IsCurrent(attempt)
                    || loginWithDeviceContext.DisposeRequested
                    || _timeProvider.GetUtcNow()
                        >= loginWithDeviceContext.Binding.ExpiresAtUtc)
                {
                    AbandonAuthenticationAttempt(attempt);
                    return false;
                }

                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        ct,
                        attempt.CancellationToken);
                    await Identity(loginWithDeviceContext.Binding.Endpoints)
                        .SendTwoFactorEmailLoginAsync(
                            CreateLoginWithDeviceEmailRequest(
                                loginWithDeviceContext),
                            linked.Token)
                        .ConfigureAwait(false);
                    return _session.IsCurrent(attempt)
                        && !loginWithDeviceContext.DisposeRequested;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    AbandonAuthenticationAttempt(attempt);
                    throw;
                }
                catch (Exception ex) when (IsExpectedAuthException(ex))
                {
                    if (!_session.IsCurrent(attempt))
                        AbandonAuthenticationAttempt(attempt);
                    return false;
                }
            }
        }
        if (TryGetPendingSsoTwoFactor(attempt, out var ssoContext))
        {
            if (!_session.IsCurrent(attempt))
                return false;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    ct, attempt.CancellationToken);
                await Identity(ssoContext.Endpoints).SendTwoFactorEmailLoginAsync(
                    CreateSsoEmailRequest(ssoContext),
                    linked.Token);
                return true;
            }
            catch (Exception ex) when (IsExpectedAuthException(ex))
            {
                if (ct.IsCancellationRequested || !_session.IsCurrent(attempt))
                    AbandonAuthenticationAttempt(attempt);
                return false;
            }
        }

        if (!TryGetPendingTwoFactor(attempt, out var context)
            || context.RequiresDeviceVerification
            || !_session.IsCurrent(attempt))
            return false;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, attempt.CancellationToken);
            await Identity(context.Endpoints).SendTwoFactorEmailLoginAsync(
                new SendEmailLoginRequest(
                    context.Email,
                    context.PasswordHash,
                    context.DeviceIdentifier),
                linked.Token);
            return true;
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            if (ct.IsCancellationRequested || !_session.IsCurrent(attempt))
                AbandonAuthenticationAttempt(attempt);
            return false;
        }
    }

    private async Task<AuthResult> CompleteLoginAsync(
        PendingTwoFactorContext context,
        TokenResponse token,
        CancellationToken ct)
    {
        try
        {
            if (!_session.IsCurrent(context.Attempt))
                return AuthenticationExpired();
            if (string.IsNullOrWhiteSpace(token.RefreshToken)
                || !AccessTokenSubjectReader.TryReadAccountId(token.AccessToken, out var accountId))
                return InvalidResponseFailure();
            if (AccessTokenSubjectReader.TryReadEmail(
                    token.AccessToken,
                    out var tokenEmail)
                && !string.Equals(
                    MasterPasswordSaltPolicy.NormalizeEmail(tokenEmail),
                    context.Email,
                    StringComparison.Ordinal))
            {
                return InvalidResponseFailure();
            }
            if (GetAccountDeletionUnlockBlock(accountId, context.Endpoints) is { } deletionBlock)
                return deletionBlock;

            var persistedSecurityVersionFloor =
                _tokenStore.TryLoad(
                    accountId,
                    context.Endpoints,
                    out var priorSession)
                && priorSession.HasValidBinding
                    ? priorSession.UserKeySecurityVersion
                    : 1;
            var persistedAccountDataRemoved = false;
            if (token.ForcePasswordReset)
            {
                lock (_authenticationTransitionGate)
                {
                    if (!_session.IsCurrent(context.Attempt))
                        return AuthenticationExpired();
                    if (!TryRemovePersistedAccountData(accountId, context.Endpoints))
                    {
                        return SafeFailure(
                            "The previous saved session could not be removed securely.");
                    }
                    if (!_session.TryClearMatchingAccountForAuthentication(
                            context.Attempt,
                            accountId,
                            context.Endpoints))
                    {
                        return AuthenticationExpired();
                    }
                    persistedAccountDataRemoved = true;
                }
            }

            PasswordChangeRequirement? passwordChange;
            try
            {
                passwordChange = ResolvePasswordChangeRequirement(
                    token,
                    context.PasswordEvidence);
            }
            catch (InvalidDataException)
            {
                return InvalidResponseFailure();
            }

            AccountKeyKind accountKeyKind;
            MasterPasswordUnlockMaterial unlock;
            PersistedAccountCryptographicState? accountCryptographicState;
            try
            {
                accountKeyKind = ClassifyAccountKeys(token);
                if (accountKeyKind == AccountKeyKind.Invalid
                    || (accountKeyKind == AccountKeyKind.LegacyV1
                        && persistedSecurityVersionFloor >= 2))
                {
                    return InvalidResponseFailure();
                }
                unlock = ResolvePasswordLoginUnlockMaterial(context, token);
                accountCryptographicState = CaptureAccountCryptographicState(
                    token,
                    accountKeyKind);
            }
            catch (Exception ex) when (IsExpectedAuthException(ex))
            {
                return InvalidResponseFailure();
            }

            SymmetricCryptoKey? derivedStretchedKey = null;
            var stretchedKey = context.StretchedKey;
            try
            {
                if (unlock.KdfType != context.KdfType
                    || unlock.KdfIterations != context.KdfIterations
                    || unlock.KdfMemory != context.KdfMemory
                    || unlock.KdfParallelism != context.KdfParallelism
                    || !string.Equals(
                        unlock.MasterPasswordSalt,
                        context.MasterPasswordSalt,
                        StringComparison.Ordinal))
                {
                    derivedStretchedKey = DeriveStretchedKey(
                        context.MasterPassword,
                        unlock.MasterPasswordSalt,
                        unlock.KdfType,
                        unlock.KdfIterations,
                        unlock.KdfMemory,
                        unlock.KdfParallelism);
                    stretchedKey = derivedStretchedKey;
                }

                if (passwordChange is not null)
                {
                    if (accountKeyKind != AccountKeyKind.LegacyV1)
                    {
                        if (!persistedAccountDataRemoved)
                        {
                            lock (_authenticationTransitionGate)
                            {
                                if (!_session.IsCurrent(context.Attempt))
                                    return AuthenticationExpired();
                                if (!TryRemovePersistedAccountData(accountId, context.Endpoints))
                                {
                                    return SafeFailure(
                                        "The previous saved session could not be removed securely.");
                                }
                                if (!_session.TryClearMatchingAccountForAuthentication(
                                        context.Attempt,
                                        accountId,
                                        context.Endpoints))
                                {
                                    return AuthenticationExpired();
                                }
                            }
                        }

                        return SafeFailure(
                            "This V2 account requires a master-password update that this client cannot complete yet.");
                    }

                    SymmetricCryptoKey restrictedUserKey;
                    try
                    {
                        restrictedUserKey = _crypto.DecryptUserKey(
                            stretchedKey,
                            EncString.Parse(unlock.ProtectedUserKey));
                    }
                    catch (Exception ex) when (IsExpectedAuthException(ex))
                    {
                        return InvalidResponseFailure();
                    }

                    return BeginRequiredPasswordChange(
                        context with
                        {
                            KdfType = unlock.KdfType,
                            KdfIterations = unlock.KdfIterations,
                            KdfMemory = unlock.KdfMemory,
                            KdfParallelism = unlock.KdfParallelism,
                            MasterPasswordSalt = unlock.MasterPasswordSalt,
                        },
                        token,
                        accountId,
                        restrictedUserKey,
                        passwordChange,
                        persistedAccountDataRemoved);
                }

                VaultUserKey userKey;
                string? wrappedPrivateKey;
                try
                {
                    var securityVersion = accountKeyKind == AccountKeyKind.V2 ? 2 : 1;
                    userKey = UnwrapUserKey(
                        stretchedKey,
                        unlock.ProtectedUserKey,
                        securityVersion,
                        unlock.UserKeyId);
                    try
                    {
                        wrappedPrivateKey = ValidateAccountCryptographicState(
                            userKey,
                            accountCryptographicState);
                    }
                    catch
                    {
                        userKey.Dispose();
                        throw;
                    }
                }
                catch (Exception ex) when (IsExpectedAuthException(ex))
                {
                    return InvalidResponseFailure();
                }

                if (!_session.TryBeginAccountSession(
                        context.Attempt,
                        accountId,
                        context.Email,
                        context.Endpoints,
                        out var lease))
                {
                    userKey.Dispose();
                    return AuthenticationExpired();
                }
                var persisted = new PersistedSession(
                    PersistedSession.CurrentSchemaVersion,
                    accountId,
                    context.Endpoints,
                    lease!.Generation,
                    context.Email,
                    context.DeviceIdentifier,
                    token.RefreshToken,
                    unlock.ProtectedUserKey,
                    unlock.KdfType,
                    unlock.KdfIterations,
                    unlock.KdfMemory,
                    unlock.KdfParallelism,
                    unlock.MasterPasswordSalt)
                {
                    TwoFactorToken = ResolveRememberToken(context, token),
                    HasMasterPassword = token.UserDecryptionOptions?.HasMasterPassword ?? true,
                    SupportsDeviceTrust = token.UserDecryptionOptions?.TrustedDeviceOption is not null,
                    UserKeySecurityVersion = userKey.SecurityVersion,
                    UserKeyId = userKey.KeyId,
                    AccountCryptographicState = accountCryptographicState,
                };

                var transferred = false;
                try
                {
                    if (!_session.TryCommit(lease!, () =>
                    {
                        _tokenStore.Save(persisted);
                        TryClearMatchingQuarantineAfterSessionPersisted(
                            context.Endpoints,
                            context.Email,
                            context.DeviceIdentifier);
                        _session.SetTokens(token.AccessToken, token.RefreshToken);
                        _session.SetUnlockedKey(userKey);
                        _session.SetEncryptedPrivateKey(wrappedPrivateKey);
                        _deviceTrust?.SetCurrentSessionCapability(
                            lease,
                            token.UserDecryptionOptions?.TrustedDeviceOption is not null);
                        transferred = true;
                    }))
                        return SessionExpiredFailure();
                }
                catch (Exception ex) when (IsExpectedPersistenceException(ex))
                {
                    _session.TryCommit(lease!, _session.Logout);
                    return SafeFailure(
                        "The authenticated session could not be stored securely.");
                }
                finally
                {
                    if (!transferred)
                        userKey.Dispose();
                }

                try
                {
                    await _bootstrapper.BootstrapAsync(lease!, ct).ConfigureAwait(false);
                    return _session.IsUnlocked(lease!)
                        ? new AuthResult.Success(lease!)
                        : SessionExpiredFailure();
                }
                catch (Exception ex) when (
                    ex is OperationCanceledException
                        || IsExpectedAuthException(ex))
                {
                    await LockAsync(lease!, CancellationToken.None).ConfigureAwait(false);
                    return SafeFailure(
                        "登录成功，但加载密码库数据失败，请重新解锁重试。");
                }
            }
            finally
            {
                derivedStretchedKey?.Dispose();
            }
        }
        finally
        {
            DisposePendingPasswordSecrets(context);
            _session.AbandonAuthenticationAttempt(context.Attempt);
        }
    }

    private PasswordChangeRequirement? ResolvePasswordChangeRequirement(
        TokenResponse token,
        MasterPasswordEvidence evidence)
    {
        MasterPasswordPolicyRequirements? policy = null;
        if (token.MasterPasswordPolicy is { } response)
        {
            policy = new MasterPasswordPolicyRequirements(
                response.MinComplexity,
                response.MinLength,
                response.RequireUpper ?? false,
                response.RequireLower ?? false,
                response.RequireNumbers ?? false,
                response.RequireSpecial ?? false,
                response.EnforceOnLogin ?? false);
            if (!policy.HasValidProtocolValues)
                throw new InvalidDataException("Master-password policy values are invalid.");
        }

        if (token.ForcePasswordReset)
        {
            return new PasswordChangeRequirement(
                RequiredPasswordChangeReason.AdministratorTemporaryPassword,
                policy);
        }

        return policy?.EnforceOnLogin == true
            && !_passwordPolicyEvaluator.Evaluate(policy, evidence)
                ? new PasswordChangeRequirement(
                    RequiredPasswordChangeReason.WeakMasterPassword,
                    policy)
                : null;
    }

    private AuthResult BeginRequiredPasswordChange(
        PendingTwoFactorContext login,
        TokenResponse token,
        string accountId,
        SymmetricCryptoKey userKey,
        PasswordChangeRequirement requirement,
        bool persistedAccountDataAlreadyRemoved)
    {
        lock (_authenticationTransitionGate)
        {
            if (!_session.IsCurrent(login.Attempt))
            {
                userKey.Dispose();
                return AuthenticationExpired();
            }

            try
            {
                if (!persistedAccountDataAlreadyRemoved
                    && !TryRemovePersistedAccountData(accountId, login.Endpoints))
                {
                    userKey.Dispose();
                    return SafeFailure(
                        "The previous saved session could not be removed securely.");
                }
            }
            catch
            {
                userKey.Dispose();
                throw;
            }

            if (!_session.TryBeginAccountSession(
                    login.Attempt,
                    accountId,
                    login.Email,
                    login.Endpoints,
                    out var lease))
            {
                userKey.Dispose();
                return AuthenticationExpired();
            }

            var pending = new PendingRequiredPasswordChangeContext(
                lease!,
                login.Email,
                login.PasswordHash,
                login.KdfType,
                login.KdfIterations,
                login.KdfMemory,
                login.KdfParallelism,
                login.MasterPasswordSalt,
                requirement.Reason,
                requirement.Policy,
                userKey);

            PendingRequiredPasswordChangeContext? prior = null;
            if (!_session.TryCommit(lease!, () =>
            {
                _session.SetTokens(token.AccessToken, token.RefreshToken);
                _session.SetEncryptedPrivateKey(null);
                _session.SetState(VaultState.PasswordChangeRequired);
                lock (_pendingGate)
                {
                    prior = _pendingRequiredPasswordChange;
                    _pendingRequiredPasswordChange = pending;
                }
            }))
            {
                pending.Dispose();
                return SessionExpiredFailure();
            }

            prior?.Dispose();

            return new AuthResult.PasswordChangeRequired(
                lease!,
                requirement.Reason,
                requirement.Policy);
        }
    }

    private async Task<AuthResult> CompleteSsoAuthenticationAsync(
        PendingSsoTwoFactorContext context,
        TokenResponse token,
        CancellationToken ct)
    {
        try
        {
            if (!_session.IsCurrent(context.Attempt))
                return AuthenticationExpired();
            if (string.IsNullOrWhiteSpace(token.RefreshToken)
                || !AccessTokenSubjectReader.TryReadAccountId(token.AccessToken, out var accountId))
                return InvalidResponseFailure();
            if (GetAccountDeletionUnlockBlock(accountId, context.Endpoints) is { } deletionBlock)
                return deletionBlock;
            if (token.ForcePasswordReset)
            {
                lock (_authenticationTransitionGate)
                {
                    if (!_session.IsCurrent(context.Attempt))
                        return AuthenticationExpired();
                    if (!TryRemovePersistedAccountData(accountId, context.Endpoints))
                    {
                        return SafeFailure(
                            "The previous saved session could not be removed securely.");
                    }
                    return SafeFailure(
                        "This account must replace its temporary password. Sign in with the temporary password to continue.");
                }
            }

            var email = context.Email;
            if (AccessTokenSubjectReader.TryReadEmail(token.AccessToken, out var tokenEmail))
            {
                email = MasterPasswordSaltPolicy.Resolve(tokenEmail, explicitSalt: null);
                context = context with { AuthenticatedEmail = email };
            }

            var decryptionOptions = token.UserDecryptionOptions;
            var trustedDevice = decryptionOptions?.TrustedDeviceOption;
            var keyConnector = decryptionOptions?.KeyConnectorOption;
            if (trustedDevice is not null && keyConnector is not null)
                return InvalidResponseFailure();

            var pendingJitResult = TryBeginPendingTdeV2JitProvisioning(
                context,
                token,
                trustedDevice,
                accountId,
                email);
            if (pendingJitResult is not null)
                return pendingJitResult;

            if (trustedDevice is { IsTdeOffboarding: true })
            {
                return new AuthResult.Failure(
                    "This account must complete trusted-device offboarding before it can be unlocked.",
                    AuthFailureKind.UnsupportedSsoDecryption);
            }

            var canUseMasterPassword =
                decryptionOptions?.MasterPasswordUnlock is not null
                || decryptionOptions?.HasMasterPassword == true;
            if (IsTdeV2JitCandidate(context, token, decryptionOptions, trustedDevice, keyConnector))
            {
                using var capabilityLinked = CancellationTokenSource.CreateLinkedTokenSource(
                    ct,
                    context.Attempt.CancellationToken);
                var config = await _clients
                    .GetAnonymous<IServerConfigApiClient>(context.Endpoints)
                    .GetConfigAsync(capabilityLinked.Token)
                    .ConfigureAwait(false);
                if (!VaultCapabilityResolver.IsTdeV2JitProvisioningEnabled(config))
                {
                    return new AuthResult.Failure(
                        "This server has not enabled V2 TDE JIT provisioning.",
                        AuthFailureKind.UnsupportedSsoDecryption);
                }

                return BeginTdeV2JitProvisioning(
                    context,
                    token,
                    accountId,
                    email,
                    context.OrganizationIdentifier!);
            }
            var accountKeyKind = ClassifyAccountKeys(token);
            if (trustedDevice is not null && accountKeyKind == AccountKeyKind.Invalid)
            {
                if (!canUseMasterPassword)
                {
                    return new AuthResult.Failure(
                        "This account uses a trusted-device encryption format that is not supported by this client.",
                        AuthFailureKind.UnsupportedSsoDecryption);
                }

                trustedDevice = null;
            }

            if (_deviceTrust is not null
                && trustedDevice is not null
                && (string.IsNullOrWhiteSpace(trustedDevice.EncryptedPrivateKey)
                    || string.IsNullOrWhiteSpace(trustedDevice.EncryptedUserKey)))
            {
                try
                {
                    _deviceTrust.RecordTrustLoss(
                        accountId,
                        context.Endpoints,
                        context.DeviceIdentifier);
                }
                catch (DeviceTrustException)
                {
                    return SafeFailure(
                        "This device is no longer trusted. Ask an existing device or administrator to approve it again.");
                }
            }

            if (_deviceTrust is not null
                && trustedDevice?.EncryptedPrivateKey is { Length: > 0 } encryptedPrivateKey
                && trustedDevice.EncryptedUserKey is { Length: > 0 } encryptedUserKey)
            {
                var supportsEnvelope = IsSupportedTrustedDeviceEnvelope(
                    encryptedPrivateKey,
                    encryptedUserKey);
                if (!supportsEnvelope && !canUseMasterPassword)
                {
                    return new AuthResult.Failure(
                        "This account uses a trusted-device encryption format that is not supported by this client.",
                        AuthFailureKind.UnsupportedSsoDecryption);
                }
                if (supportsEnvelope)
                {
                    try
                    {
                        if (_deviceTrust.TryDecryptUserKey(
                                accountId,
                                context.Endpoints,
                                context.DeviceIdentifier,
                                encryptedPrivateKey,
                                encryptedUserKey,
                                out var trustedUserKey))
                        {
                            return await CompleteTrustedDeviceLoginAsync(
                                context,
                                token,
                                accountKeyKind,
                                accountId,
                                email,
                                trustedUserKey,
                                ct);
                        }
                    }
                    catch (DeviceTrustException) when (canUseMasterPassword)
                    {
                        // A stale local device key may fall back only when the server
                        // explicitly offers master-password decryption.
                    }
                    catch (DeviceTrustException)
                    {
                        return SafeFailure(
                            "This device is no longer trusted. Ask an existing device or administrator to approve it again.");
                    }
                }
            }

            if (decryptionOptions is { HasMasterPassword: false })
            {
                var keyConnectorUrl = keyConnector?.KeyConnectorUrl;
                if (_keyConnector is not null
                    && !string.IsNullOrWhiteSpace(token.Key)
                    && KeyConnectorEndpointPolicy.TryNormalize(keyConnectorUrl, out var normalizedUrl))
                {
                    if (!IsSupportedLegacyKeyConnectorAccountShape(token))
                    {
                        return new AuthResult.Failure(
                            "This account uses a Key Connector encryption format that is not supported by this client.",
                            AuthFailureKind.UnsupportedSsoDecryption);
                    }
                    return await CompleteKeyConnectorLoginAsync(
                        context,
                        token,
                        accountId,
                        email,
                        normalizedUrl,
                        ct);
                }

                if (trustedDevice is not null
                    && (trustedDevice.HasLoginApprovingDevice == true
                        || trustedDevice.HasAdminApproval == true))
                {
                    return BeginTrustedDeviceApproval(
                        context,
                        token,
                        accountKeyKind,
                        accountId,
                        email,
                        trustedDevice.HasAdminApproval == true,
                        trustedDevice.HasLoginApprovingDevice == true);
                }

                return new AuthResult.Failure(
                    "This SSO account requires an unsupported decryption method.",
                    AuthFailureKind.UnsupportedSsoDecryption);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, context.Attempt.CancellationToken);
            var unlock = await ResolveSsoUnlockMaterialAsync(
                context.Endpoints,
                email,
                token,
                linked.Token);
            if (accountKeyKind == AccountKeyKind.Invalid)
                return InvalidResponseFailure();
            var accountCryptographicState = CaptureAccountCryptographicState(
                token,
                accountKeyKind);
            if (accountKeyKind == AccountKeyKind.V2)
            {
                ValidateV2UserKeyIdAgainstAccountCryptographicState(
                    unlock.UserKeyId,
                    accountCryptographicState!);
            }
            _ = EncString.Parse(unlock.ProtectedUserKey);

            if (!_session.TryBeginAccountSession(
                    context.Attempt,
                    accountId,
                    email,
                    context.Endpoints,
                    out var unlockingLease))
                return AuthenticationExpired();
            if (!_session.TryLockIfCurrent(unlockingLease!, out var lockedLease))
                return SessionExpiredFailure();

            var persisted = new PersistedSession(
                PersistedSession.CurrentSchemaVersion,
                accountId,
                context.Endpoints,
                lockedLease!.Generation,
                email,
                context.DeviceIdentifier,
                token.RefreshToken,
                unlock.ProtectedUserKey,
                unlock.KdfType,
                unlock.KdfIterations,
                unlock.KdfMemory,
                unlock.KdfParallelism,
                unlock.MasterPasswordSalt)
            {
                TwoFactorToken = ResolveSsoRememberToken(context, token),
                HasMasterPassword = token.UserDecryptionOptions?.HasMasterPassword,
                SupportsDeviceTrust = trustedDevice is not null,
                UserKeySecurityVersion = accountKeyKind == AccountKeyKind.V2 ? 2 : 1,
                UserKeyId = accountKeyKind == AccountKeyKind.V2
                    ? unlock.UserKeyId
                    : null,
                AccountCryptographicState = accountCryptographicState,
            };

            try
            {
                if (!_session.TryCommit(lockedLease, () =>
                {
                    _tokenStore.Save(persisted);
                    TryClearMatchingQuarantineAfterSessionPersisted(
                        context.Endpoints,
                        context.AuthenticatedEmail,
                        context.DeviceIdentifier);
                    _deviceTrust?.SetCurrentSessionCapability(
                        lockedLease,
                        trustedDevice is not null);
                }))
                    return SessionExpiredFailure();
            }
            catch (Exception ex) when (IsExpectedPersistenceException(ex))
            {
                _session.TryCommit(lockedLease, _session.Logout);
                return SafeFailure("The authenticated session could not be stored securely.");
            }

            return new AuthResult.SsoAuthenticated(lockedLease);
        }
        finally
        {
            lock (_pendingGate)
            {
                if (_pendingSsoTwoFactor?.Attempt == context.Attempt)
                    _pendingSsoTwoFactor = null;
            }
            _session.AbandonAuthenticationAttempt(context.Attempt);
        }
    }

    private MasterPasswordUnlockMaterial ResolvePasswordLoginUnlockMaterial(
        PendingTwoFactorContext context,
        TokenResponse token)
    {
        var passwordUnlock = token.UserDecryptionOptions?.MasterPasswordUnlock;
        var protectedUserKey = ResolveMasterKeyWrappedUserKey(passwordUnlock, token.Key);

        var kdfType = context.KdfType;
        var iterations = context.KdfIterations;
        var memory = context.KdfMemory;
        var parallelism = context.KdfParallelism;
        var salt = context.MasterPasswordSalt;
        if ((passwordUnlock?.Kdf is null) != (passwordUnlock?.Salt is null))
        {
            throw new InvalidDataException(
                "Master-password unlock KDF settings and salt must be supplied together.");
        }
        if (passwordUnlock?.Kdf is not null && passwordUnlock.Salt is not null)
        {
            var resolved = new PreloginResponse(
                null,
                null,
                null,
                null,
                passwordUnlock.Kdf,
                passwordUnlock.Salt).Resolve(context.Email);
            kdfType = resolved.KdfType;
            iterations = resolved.Iterations;
            memory = resolved.Memory;
            parallelism = resolved.Parallelism;
            salt = resolved.MasterPasswordSalt;
        }

        CryptoService.ValidateKdfParameters(
            kdfType,
            iterations,
            memory,
            parallelism);

        return new MasterPasswordUnlockMaterial(
            protectedUserKey,
            kdfType,
            iterations,
            memory,
            parallelism,
            salt,
            NormalizeOptionalUserKeyId(passwordUnlock?.ContainedKeyId));
    }

    private MasterPasswordUnlockMaterial ResolveTokenMasterPasswordUnlockMaterial(
        string email,
        TokenResponse token)
    {
        var passwordUnlock = token.UserDecryptionOptions?.MasterPasswordUnlock;
        var protectedUserKey = ResolveMasterKeyWrappedUserKey(passwordUnlock, token.Key);
        if ((passwordUnlock?.Kdf is null) != (passwordUnlock?.Salt is null))
        {
            throw new InvalidDataException(
                "Master-password unlock KDF settings and salt must be supplied together.");
        }

        ResolvedPreloginSettings resolved;
        if (passwordUnlock?.Kdf is not null && passwordUnlock.Salt is not null)
        {
            resolved = new PreloginResponse(
                null,
                null,
                null,
                null,
                passwordUnlock.Kdf,
                passwordUnlock.Salt).Resolve(email);
        }
        else
        {
            resolved = new ResolvedPreloginSettings(
                token.Kdf,
                token.KdfIterations,
                token.KdfMemory,
                token.KdfParallelism,
                MasterPasswordSaltPolicy.Resolve(email, explicitSalt: null));
        }

        CryptoService.ValidateKdfParameters(
            resolved.KdfType,
            resolved.Iterations,
            resolved.Memory,
            resolved.Parallelism);
        return new MasterPasswordUnlockMaterial(
            protectedUserKey,
            resolved.KdfType,
            resolved.Iterations,
            resolved.Memory,
            resolved.Parallelism,
            resolved.MasterPasswordSalt,
            NormalizeOptionalUserKeyId(passwordUnlock?.ContainedKeyId));
    }

    private MasterPasswordUnlockMaterial? ResolveOptionalTokenMasterPasswordUnlockMaterial(
        string email,
        TokenResponse token)
    {
        if (token.UserDecryptionOptions?.MasterPasswordUnlock is null)
        {
            if (token.UserDecryptionOptions?.HasMasterPassword == false)
                return null;
        }

        return ResolveTokenMasterPasswordUnlockMaterial(email, token);
    }

    private async Task<MasterPasswordUnlockMaterial> ResolveSsoUnlockMaterialAsync(
        ServerEndpoints endpoints,
        string email,
        TokenResponse token,
        CancellationToken ct)
    {
        var passwordUnlock = token.UserDecryptionOptions?.MasterPasswordUnlock;
        var protectedUserKey = ResolveMasterKeyWrappedUserKey(passwordUnlock, token.Key);

        ResolvedPreloginSettings resolved;
        if ((passwordUnlock?.Kdf is null) != (passwordUnlock?.Salt is null))
        {
            throw new InvalidDataException(
                "Master-password unlock KDF settings and salt must be supplied together.");
        }
        if (passwordUnlock?.Kdf is not null && passwordUnlock.Salt is not null)
        {
            resolved = new PreloginResponse(
                null,
                null,
                null,
                null,
                passwordUnlock.Kdf,
                passwordUnlock.Salt).Resolve(email);
        }
        else
        {
            var prelogin = await Identity(endpoints).PreloginAsync(email, ct);
            resolved = prelogin.Resolve(email);
        }

        CryptoService.ValidateKdfParameters(
            resolved.KdfType,
            resolved.Iterations,
            resolved.Memory,
            resolved.Parallelism);

        return new MasterPasswordUnlockMaterial(
            protectedUserKey,
            resolved.KdfType,
            resolved.Iterations,
            resolved.Memory,
            resolved.Parallelism,
            resolved.MasterPasswordSalt,
            NormalizeOptionalUserKeyId(passwordUnlock?.ContainedKeyId));
    }

    private static string ResolveMasterKeyWrappedUserKey(
        MasterPasswordUnlockResponse? passwordUnlock,
        string? legacyTopLevelKey)
    {
        string? resolved = null;
        foreach (var candidate in new[]
        {
            passwordUnlock?.MasterKeyWrappedUserKey,
            passwordUnlock?.MasterKeyEncryptedUserKey,
            legacyTopLevelKey,
        })
        {
            if (candidate is null)
                continue;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                throw new InvalidDataException(
                    "The master-key-wrapped user key is empty.");
            }
            if (resolved is not null
                && !string.Equals(resolved, candidate, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The response contains conflicting master-key-wrapped user keys.");
            }

            resolved = candidate;
        }

        var protectedUserKey = resolved
            ?? throw new InvalidDataException(
                "The response has no master-key-wrapped user key.");
        if (!IsType2EncString(protectedUserKey))
        {
            throw new InvalidDataException(
                "The master-key-wrapped user key must use the authenticated legacy envelope.");
        }

        return protectedUserKey;
    }

    private AuthResult BeginTrustedDeviceApproval(
        PendingSsoTwoFactorContext context,
        TokenResponse token,
        AccountKeyKind accountKeyKind,
        string accountId,
        string email,
        bool hasAdminApproval,
        bool hasLoginApprovingDevice)
    {
        PersistedAccountCryptographicState? accountCryptographicState;
        MasterPasswordUnlockMaterial? masterPasswordUnlock;
        try
        {
            accountCryptographicState = CaptureAccountCryptographicState(
                token,
                accountKeyKind);
            masterPasswordUnlock = ResolveOptionalTokenMasterPasswordUnlockMaterial(
                email,
                token);
            if (accountKeyKind == AccountKeyKind.V2
                && masterPasswordUnlock is not null)
            {
                ValidateV2UserKeyIdAgainstAccountCryptographicState(
                    masterPasswordUnlock.UserKeyId,
                    accountCryptographicState!);
            }
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            return InvalidResponseFailure();
        }

        if (!_session.TryBeginAccountSession(
                context.Attempt,
                accountId,
                email,
                context.Endpoints,
                out var lease))
        {
            return AuthenticationExpired();
        }

        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            accountId,
            context.Endpoints,
            lease!.Generation,
            email,
            context.DeviceIdentifier,
            token.RefreshToken,
            masterPasswordUnlock?.ProtectedUserKey ?? string.Empty,
            masterPasswordUnlock?.KdfType ?? token.Kdf,
            masterPasswordUnlock?.KdfIterations ?? token.KdfIterations,
            masterPasswordUnlock?.KdfMemory ?? token.KdfMemory,
            masterPasswordUnlock?.KdfParallelism ?? token.KdfParallelism,
            masterPasswordUnlock?.MasterPasswordSalt
                ?? MasterPasswordSaltPolicy.NormalizeEmail(email),
            masterPasswordUnlock is null
                ? PersistedSessionUnlockMethod.TrustedDeviceSso
                : PersistedSessionUnlockMethod.MasterPassword)
        {
            TwoFactorToken = ResolveSsoRememberToken(context, token),
            HasMasterPassword = token.UserDecryptionOptions?.HasMasterPassword,
            SupportsDeviceTrust = true,
            UserKeySecurityVersion = accountKeyKind == AccountKeyKind.V2 ? 2 : 1,
            UserKeyId = accountKeyKind == AccountKeyKind.V2
                ? masterPasswordUnlock?.UserKeyId
                : null,
            AccountCryptographicState = accountCryptographicState,
        };

        try
        {
            if (!_session.TryCommit(lease, () =>
            {
                _tokenStore.Save(persisted);
                TryClearMatchingQuarantineAfterSessionPersisted(
                    context.Endpoints,
                    context.AuthenticatedEmail,
                    context.DeviceIdentifier);
                _session.SetTokens(token.AccessToken, token.RefreshToken);
                _session.SetEncryptedPrivateKey(
                    accountCryptographicState?.WrappedPrivateKey ?? token.PrivateKey);
                _session.SetState(VaultState.Locked);
                _deviceTrust?.SetCurrentSessionCapability(lease, supported: true);
                lock (_pendingGate)
                {
                    _pendingTrustedDeviceAccount = new PendingTrustedDeviceAccountState(
                        lease,
                        accountKeyKind,
                        token.AccountKeys,
                        masterPasswordUnlock?.UserKeyId);
                }
            }))
            {
                return SessionExpiredFailure();
            }
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            _session.TryCommit(lease, _session.Logout);
            return SafeFailure("The authenticated session could not be stored securely.");
        }

        return new AuthResult.TrustedDeviceApprovalRequired(
            lease,
            hasAdminApproval,
            hasLoginApprovingDevice);
    }

    private AuthResult BeginTdeV2JitProvisioning(
        PendingSsoTwoFactorContext context,
        TokenResponse token,
        string accountId,
        string email,
        string organizationIdentifier,
        bool? requiredTrustCurrentDevice = null)
    {
        if (!_session.TryBeginAccountSession(
                context.Attempt,
                accountId,
                email,
                context.Endpoints,
                out var lease))
        {
            return AuthenticationExpired();
        }

        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            accountId,
            context.Endpoints,
            lease!.Generation,
            email,
            context.DeviceIdentifier,
            token.RefreshToken,
            string.Empty,
            token.Kdf,
            token.KdfIterations,
            token.KdfMemory,
            token.KdfParallelism,
            MasterPasswordSaltPolicy.NormalizeEmail(email),
            PersistedSessionUnlockMethod.TrustedDeviceSso)
        {
            TwoFactorToken = ResolveSsoRememberToken(context, token),
            HasMasterPassword = token.UserDecryptionOptions?.HasMasterPassword,
        };

        try
        {
            if (!_session.TryCommit(lease, () =>
            {
                _tokenStore.Save(persisted);
                TryClearMatchingQuarantineAfterSessionPersisted(
                    context.Endpoints,
                    context.AuthenticatedEmail,
                    context.DeviceIdentifier);
                _session.SetTokens(token.AccessToken, token.RefreshToken);
                _session.SetState(VaultState.Locked);
                _deviceTrust?.SetCurrentSessionCapability(lease, supported: false);
            }))
            {
                return SessionExpiredFailure();
            }
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            _session.TryCommit(lease, _session.Logout);
            return SafeFailure("The authenticated session could not be stored securely.");
        }

        return new AuthResult.TdeV2JitProvisioningRequired(
            lease,
            organizationIdentifier,
            requiredTrustCurrentDevice);
    }

    private AuthResult? TryBeginPendingTdeV2JitProvisioning(
        PendingSsoTwoFactorContext context,
        TokenResponse token,
        TrustedDeviceUserDecryptionOptionResponse? trustedDevice,
        string accountId,
        string email)
    {
        if (_tdeV2JitPendingStore is null
            || !IsActiveTrustedDeviceOption(trustedDevice))
            return null;

        TdeV2JitPendingStoreRecord? record = null;
        byte[]? userKeyTransport = null;
        try
        {
            var binding = new DeviceKeyBinding(
                accountId,
                context.Endpoints,
                context.DeviceIdentifier);
            var read = _tdeV2JitPendingStore.Read(binding);
            if (read.Status == TdeV2JitPendingReadStatus.Absent)
                return null;
            if (read.Status != TdeV2JitPendingReadStatus.Found
                || read.Record is null)
            {
                return SafeFailure(
                    "The saved V2 TDE JIT recovery state is unavailable. No account keys were changed.");
            }
            record = read.Record;

            if (string.IsNullOrWhiteSpace(context.OrganizationIdentifier)
                || !string.Equals(
                    context.OrganizationIdentifier,
                    record.Pending.OrganizationIdentifier,
                    StringComparison.Ordinal))
            {
                return SafeFailure(
                    "The SSO organization does not match the saved V2 TDE JIT recovery state.");
            }

            if (token.AccountKeys is { } accountKeys)
            {
                if (!TdeV2JitProvisioningService.AccountKeysMatch(
                        accountKeys,
                        record.Pending.AccountKeysRequest))
                {
                    return SafeFailure(
                        "The server account keys do not match the saved V2 TDE JIT recovery state.");
                }

                userKeyTransport = record.Pending.CopyUserKeyTransport();
                using (var userKey = V2UserKey.ImportTransport(userKeyTransport))
                {
                    var validated = _v2AccountKeysValidator.Validate(accountKeys, userKey);
                    if (!string.Equals(
                            validated.UserKeyId,
                            record.Pending.AccountKeysRequest.UserKeyId,
                            StringComparison.Ordinal))
                    {
                        return SafeFailure(
                            "The saved V2 user key does not match the server account keys.");
                    }
                }

                if (record.Stage < TdeV2JitPendingStage.AccountKeysConfirmed)
                {
                    record = AdvancePendingJitRecord(
                        record,
                        TdeV2JitPendingStage.AccountKeysConfirmed);
                }
            }
            else if (record.Stage >= TdeV2JitPendingStage.AccountKeysConfirmed)
            {
                return SafeFailure(
                    "The server no longer reports the account keys recorded by the V2 TDE JIT recovery state.");
            }

            if (record.Stage == TdeV2JitPendingStage.AccountKeysConfirmed
                && trustedDevice?.HasAdminApproval == true)
            {
                record = AdvancePendingJitRecord(
                    record,
                    TdeV2JitPendingStage.ResetPasswordEnrolled);
            }

            var trustedRequest = record.Pending.TrustedDeviceRequest;
            if (record.Stage == TdeV2JitPendingStage.ResetPasswordEnrolled
                && trustedRequest is not null
                && string.Equals(
                    trustedDevice?.EncryptedUserKey,
                    trustedRequest.EncryptedUserKey,
                    StringComparison.Ordinal)
                && string.Equals(
                    trustedDevice?.EncryptedPrivateKey,
                    trustedRequest.EncryptedPrivateKey,
                    StringComparison.Ordinal))
            {
                record = AdvancePendingJitRecord(
                    record,
                    TdeV2JitPendingStage.TrustedDeviceEnrolled);
            }

            return BeginTdeV2JitProvisioning(
                context,
                token,
                accountId,
                email,
                record.Pending.OrganizationIdentifier,
                record.Pending.TrustCurrentDevice);
        }
        catch (Exception ex) when (ex is
            IOException
                or UnauthorizedAccessException
                or CryptographicException
                or InvalidDataException
                or ArgumentException
                or PlatformNotSupportedException)
        {
            return SafeFailure(
                "The saved V2 TDE JIT recovery state could not be verified. No new account keys were submitted.");
        }
        finally
        {
            if (userKeyTransport is not null)
                CryptographicOperations.ZeroMemory(userKeyTransport);
            record?.Dispose();
        }
    }

    private TdeV2JitPendingStoreRecord AdvancePendingJitRecord(
        TdeV2JitPendingStoreRecord record,
        TdeV2JitPendingStage nextStage)
    {
        var next = _tdeV2JitPendingStore!.TryAdvance(record, nextStage);
        return next ?? throw new InvalidDataException(
            "The V2 TDE JIT recovery state changed unexpectedly.");
    }

    private async Task<AuthResult> CompleteTrustedDeviceLoginAsync(
        PendingSsoTwoFactorContext context,
        TokenResponse token,
        AccountKeyKind accountKeyKind,
        string accountId,
        string email,
        byte[] userKeyBytes,
        CancellationToken ct)
    {
        MasterPasswordUnlockMaterial? masterPasswordUnlock;
        try
        {
            masterPasswordUnlock = ResolveOptionalTokenMasterPasswordUnlockMaterial(
                email,
                token);
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            CryptographicOperations.ZeroMemory(userKeyBytes);
            return InvalidResponseFailure();
        }

        SymmetricCryptoKey? legacyUserKey = null;
        V2UserKey? v2UserKey = null;
        byte[]? ownedUserKeyBytes = userKeyBytes;
        var wrappedPrivateKey = token.PrivateKey;
        PersistedAccountCryptographicState? accountCryptographicState = null;
        try
        {
            switch (accountKeyKind)
            {
                case AccountKeyKind.LegacyV1 when ownedUserKeyBytes.Length == 64:
                    legacyUserKey = new SymmetricCryptoKey(ownedUserKeyBytes);
                    ownedUserKeyBytes = null;
                    break;
                case AccountKeyKind.V2 when token.AccountKeys is not null:
                    v2UserKey = V2UserKey.ImportTransport(ownedUserKeyBytes);
                    CryptographicOperations.ZeroMemory(ownedUserKeyBytes);
                    ownedUserKeyBytes = null;
                    if (masterPasswordUnlock?.UserKeyId is { } expectedUserKeyId
                        && !string.Equals(
                            expectedUserKeyId,
                            v2UserKey.KeyIdHex,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "The trusted-device V2 user key does not match its contained key identifier.");
                    }
                    wrappedPrivateKey = _v2AccountKeysValidator
                        .Validate(token.AccountKeys, v2UserKey)
                        .WrappedPrivateKey;
                    break;
                default:
                    throw new DeviceTrustException(
                        "The trusted device returned an invalid user key.");
            }
            accountCryptographicState = CaptureAccountCryptographicState(
                token,
                accountKeyKind);
        }
        catch
        {
            if (ownedUserKeyBytes is not null)
                CryptographicOperations.ZeroMemory(ownedUserKeyBytes);
            legacyUserKey?.Dispose();
            v2UserKey?.Dispose();
            throw;
        }

        if (!_session.TryBeginAccountSession(
                context.Attempt,
                accountId,
                email,
                context.Endpoints,
                out var lease))
        {
            legacyUserKey?.Dispose();
            v2UserKey?.Dispose();
            return AuthenticationExpired();
        }

        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            accountId,
            context.Endpoints,
            lease!.Generation,
            email,
            context.DeviceIdentifier,
            token.RefreshToken,
            masterPasswordUnlock?.ProtectedUserKey ?? string.Empty,
            masterPasswordUnlock?.KdfType ?? token.Kdf,
            masterPasswordUnlock?.KdfIterations ?? token.KdfIterations,
            masterPasswordUnlock?.KdfMemory ?? token.KdfMemory,
            masterPasswordUnlock?.KdfParallelism ?? token.KdfParallelism,
            masterPasswordUnlock?.MasterPasswordSalt
                ?? MasterPasswordSaltPolicy.NormalizeEmail(email),
            masterPasswordUnlock is null
                ? PersistedSessionUnlockMethod.TrustedDeviceSso
                : PersistedSessionUnlockMethod.MasterPassword)
        {
            TwoFactorToken = ResolveSsoRememberToken(context, token),
            HasMasterPassword = token.UserDecryptionOptions?.HasMasterPassword,
            SupportsDeviceTrust = true,
            UserKeySecurityVersion = accountKeyKind == AccountKeyKind.V2 ? 2 : 1,
            UserKeyId = v2UserKey?.KeyIdHex,
            AccountCryptographicState = accountCryptographicState,
        };

        var transferred = false;
        try
        {
            if (!_session.TryCommit(lease, () =>
            {
                _tokenStore.Save(persisted);
                TryClearMatchingQuarantineAfterSessionPersisted(
                    context.Endpoints,
                    context.AuthenticatedEmail,
                    context.DeviceIdentifier);
                _session.SetTokens(token.AccessToken, token.RefreshToken);
                if (v2UserKey is not null)
                    _session.SetUnlockedKey(v2UserKey);
                else
                    _session.SetUnlockedKey(legacyUserKey!);
                _session.SetEncryptedPrivateKey(wrappedPrivateKey);
                _deviceTrust!.SetCurrentSessionCapability(lease, supported: true);
                transferred = true;
            }))
                return SessionExpiredFailure();
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            _session.TryCommit(lease, _session.Logout);
            return SafeFailure(
                "The authenticated session could not be stored securely.");
        }
        finally
        {
            if (!transferred)
            {
                legacyUserKey?.Dispose();
                v2UserKey?.Dispose();
            }
        }

        var fromCache = TryLoadCurrentVaultCache(lease);

        using var sessionLinked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, lease.CancellationToken);
        try
        {
            await _bootstrapper.BootstrapAsync(lease, sessionLinked.Token).ConfigureAwait(false);
            return _session.IsUnlocked(lease)
                ? new AuthResult.Success(lease)
                : SessionExpiredFailure();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await LockAsync(lease, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or VaultApiException
            || RequestTimeoutClassifier.IsTimeout(ex, ct))
        {
            return FinishUnlockOffline(lease, fromCache);
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            await LockAsync(lease, CancellationToken.None).ConfigureAwait(false);
            return SafeFailure(
                "Authentication succeeded, but the vault could not be loaded.");
        }
    }

    private bool TryLoadCurrentVaultCache(VaultSessionLease lease)
    {
        if (!_session.TryCopyUserKeyMaterial(lease, out var copiedUserKey)
            || copiedUserKey is null)
        {
            return false;
        }

        using (copiedUserKey)
        {
            return copiedUserKey.IsV2
                ? _cache.TryLoadInto(lease, copiedUserKey.V2!)
                : _cache.TryLoadInto(lease, copiedUserKey.Legacy!);
        }
    }

    private async Task<AuthResult> CompleteKeyConnectorLoginAsync(
        PendingSsoTwoFactorContext context,
        TokenResponse token,
        string accountId,
        string email,
        string keyConnectorUrl,
        CancellationToken ct)
    {
        byte[] masterKeyBytes;
        using (var authenticationLinked = CancellationTokenSource.CreateLinkedTokenSource(
                   ct, context.Attempt.CancellationToken))
        {
            masterKeyBytes = await _keyConnector!.GetMasterKeyAsync(
                keyConnectorUrl,
                token.AccessToken,
                authenticationLinked.Token);
        }
        var userKey = DecryptLegacyKeyConnectorUserKey(masterKeyBytes, token.Key!);
        if (!_session.TryBeginAccountSession(
                context.Attempt,
                accountId,
                email,
                context.Endpoints,
                out var lease))
        {
            userKey.Dispose();
            return AuthenticationExpired();
        }

        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            accountId,
            context.Endpoints,
            lease!.Generation,
            email,
            context.DeviceIdentifier,
            token.RefreshToken,
            token.Key!,
            KdfType.Pbkdf2,
            KdfParameterPolicy.MinimumPbkdf2WriteIterations,
            null,
            null,
            MasterPasswordSaltPolicy.NormalizeEmail(email),
            PersistedSessionUnlockMethod.KeyConnector,
            keyConnectorUrl)
        {
            TwoFactorToken = ResolveSsoRememberToken(context, token),
            HasMasterPassword = token.UserDecryptionOptions?.HasMasterPassword,
        };

        var transferred = false;
        try
        {
            if (!_session.TryCommit(lease, () =>
            {
                _tokenStore.Save(persisted);
                TryClearMatchingQuarantineAfterSessionPersisted(
                    context.Endpoints,
                    context.AuthenticatedEmail,
                    context.DeviceIdentifier);
                _session.SetTokens(token.AccessToken, token.RefreshToken);
                _session.SetUnlockedKey(userKey);
                _session.SetEncryptedPrivateKey(token.PrivateKey);
                _deviceTrust?.SetCurrentSessionCapability(lease, supported: false);
                transferred = true;
            }))
                return SessionExpiredFailure();
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            _session.TryCommit(lease, _session.Logout);
            return SafeFailure(
                "The authenticated session could not be stored securely.");
        }
        finally
        {
            if (!transferred)
                userKey.Dispose();
        }

        using var sessionLinked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, lease.CancellationToken);
        try
        {
            await _bootstrapper.BootstrapAsync(lease, sessionLinked.Token).ConfigureAwait(false);
            return _session.IsUnlocked(lease)
                ? new AuthResult.Success(lease)
                : SessionExpiredFailure();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await LockAsync(lease, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (
            ex is OperationCanceledException
                or HttpRequestException
                or VaultApiException
                or CryptographicException)
        {
            await LockAsync(lease, CancellationToken.None).ConfigureAwait(false);
            return SafeFailure(
                "Authentication succeeded, but the vault could not be loaded.");
        }
    }

    // 记住设备令牌只可在同 email + origin 下复用:本次响应有则用之;remember 登录服务端不回新令牌
    // (返回 null),此时保留旧值(仅当 origin+email 一致)不清空。
    private string? ResolveRememberToken(
        PendingTwoFactorContext context,
        TokenResponse token)
    {
        if (!string.IsNullOrEmpty(token.TwoFactorToken))
            return token.TwoFactorToken;
        return _tokenStore.TryLoad(out var prior)
            && prior.Endpoints == context.Endpoints
            && string.Equals(
                MasterPasswordSaltPolicy.NormalizeEmail(prior.Email),
                context.Email,
                StringComparison.Ordinal)
            ? prior.TwoFactorToken
            : null;
    }

    private string? ResolveSsoRememberToken(
        PendingSsoTwoFactorContext context,
        TokenResponse token)
    {
        if (!string.IsNullOrEmpty(token.TwoFactorToken))
            return token.TwoFactorToken;
        return _tokenStore.TryLoad(out var prior)
            && prior.Endpoints == context.Endpoints
            && string.Equals(
                MasterPasswordSaltPolicy.NormalizeEmail(prior.Email),
                context.Email,
                StringComparison.Ordinal)
            ? prior.TwoFactorToken
            : null;
    }

    private static ConnectTokenRequest CreateSsoTokenRequest(
        PendingSsoTwoFactorContext context,
        TwoFactorPayload? twoFactor) =>
        ConnectTokenRequest.AuthorizationCode(
            context.Code,
            context.CodeVerifier,
            context.RedirectUri,
            context.DeviceIdentifier,
            DeviceName,
            twoFactor);

    private static SendEmailLoginRequest CreateSsoEmailRequest(
        PendingSsoTwoFactorContext context)
    {
        if (string.IsNullOrEmpty(context.SsoEmail2FaSessionToken))
            return new SendEmailLoginRequest(null, null, context.DeviceIdentifier);

        return new SendEmailLoginRequest(
            context.ChallengeEmail ?? context.Email,
            string.Empty,
            context.DeviceIdentifier,
            context.SsoEmail2FaSessionToken);
    }

    private static IReadOnlyDictionary<int, string> NormalizeProviderData(
        IReadOnlyDictionary<string, JsonElement?>? providerData)
    {
        if (providerData is null || providerData.Count == 0)
            return new Dictionary<int, string>();

        var result = new Dictionary<int, string>();
        foreach (var pair in providerData)
        {
            if (!int.TryParse(pair.Key, out var provider)
                || pair.Value is not JsonElement element
                || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            var json = element.GetRawText();
            if (json.Length <= 512 * 1024)
                result[provider] = json;
        }
        return result;
    }

    private static bool IsSingleUseTwoFactorProvider(int provider) =>
        provider is (int)TwoFactorProviderType.Duo
            or (int)TwoFactorProviderType.OrganizationDuo
            or (int)TwoFactorProviderType.WebAuthn;

    private PendingTwoFactorContext BuildLoginContext(
        AuthenticationAttemptLease attempt,
        ServerEndpoints endpoints,
        string email,
        string masterPassword,
        PreloginResponse prelogin,
        string deviceIdentifier)
    {
        var resolved = prelogin.Resolve(email);
        var passwordEvidence = _passwordPolicyEvaluator.CaptureEvidence(masterPassword);
        var masterKey = _crypto.DeriveMasterKey(
            masterPassword,
            resolved.MasterPasswordSalt,
            resolved.KdfType,
            resolved.Iterations,
            resolved.Memory,
            resolved.Parallelism);
        try
        {
            var passwordHash = _crypto.ComputeMasterPasswordHash(masterKey, masterPassword);
            var retainedPassword = masterPassword.ToCharArray();
            SymmetricCryptoKey? stretchedKey = null;
            try
            {
                stretchedKey = _crypto.StretchMasterKey(masterKey);
                var context = new PendingTwoFactorContext(
                    attempt,
                    endpoints,
                    email,
                    deviceIdentifier,
                    passwordHash,
                    retainedPassword,
                    stretchedKey,
                    resolved.KdfType,
                    resolved.Iterations,
                    resolved.Memory,
                    resolved.Parallelism,
                    resolved.MasterPasswordSalt,
                    passwordEvidence,
                    Array.Empty<int>(),
                    new Dictionary<int, string>(),
                    RequiresDeviceVerification: false,
                    DeviceVerificationExpiresAt: null);
                stretchedKey = null;
                retainedPassword = null!;
                return context;
            }
            finally
            {
                stretchedKey?.Dispose();
                if (retainedPassword is not null)
                    Array.Clear(retainedPassword);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private SymmetricCryptoKey DeriveStretchedKey(
        string masterPassword,
        string salt,
        KdfType kdfType,
        int iterations,
        int? memory,
        int? parallelism) => DeriveStretchedKey(
            masterPassword.AsSpan(),
            salt,
            kdfType,
            iterations,
            memory,
            parallelism);

    private SymmetricCryptoKey DeriveStretchedKey(
        char[] masterPassword,
        string salt,
        KdfType kdfType,
        int iterations,
        int? memory,
        int? parallelism) => DeriveStretchedKey(
            masterPassword.AsSpan(),
            salt,
            kdfType,
            iterations,
            memory,
            parallelism);

    private SymmetricCryptoKey DeriveStretchedKey(
        ReadOnlySpan<char> masterPassword,
        string salt,
        KdfType kdfType,
        int iterations,
        int? memory,
        int? parallelism)
    {
        var masterKey = _crypto.DeriveMasterKey(
            masterPassword,
            salt,
            kdfType,
            iterations,
            memory,
            parallelism);
        try
        {
            return _crypto.StretchMasterKey(masterKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private static AuthResult.Failure ToFailure(ConnectTokenResult result) => result switch
    {
        ConnectTokenResult.Error { HttpStatus: 400 or 401 } => new AuthResult.Failure(
            "Authentication credentials were rejected.",
            AuthFailureKind.InvalidCredentials),
        ConnectTokenResult.Error { HttpStatus: 429 } => new AuthResult.Failure(
            "The authentication server has temporarily limited requests. Try again later.",
            AuthFailureKind.RateLimited),
        ConnectTokenResult.Error => new AuthResult.Failure(
            "Authentication failed.",
            AuthFailureKind.Unspecified),
        ConnectTokenResult.TwoFactorRequired => new AuthResult.Failure(
            "Two-factor authentication is still required.",
            AuthFailureKind.InvalidCredentials),
        _ => new AuthResult.Failure(
            "Authentication failed.",
            AuthFailureKind.Unspecified),
    };

    private static bool IsLoginWithDeviceOutcomeUncertain(
        ConnectTokenResult.Error error) =>
        error.HttpStatus is 408 or >= 500;

    private static AuthResult.Failure ClassifyExpectedFailure(
        Exception exception,
        CancellationToken callerToken)
    {
        if (RequestTimeoutClassifier.IsTimeout(exception, callerToken))
        {
            return new AuthResult.Failure(
                "The authentication request timed out.",
                AuthFailureKind.Timeout);
        }

        if (exception is HttpRequestException)
        {
            return new AuthResult.Failure(
                "The authentication server could not be reached.",
                AuthFailureKind.Network);
        }

        if (exception is TaskCanceledException)
            return SessionExpiredFailure();

        return InvalidResponseFailure();
    }

    private SymmetricCryptoKey DecryptLegacyKeyConnectorUserKey(
        byte[] keyConnectorKey,
        string wrappedUserKey)
    {
        try
        {
            if (keyConnectorKey.Length != 32)
                throw new InvalidDataException(
                    "This Key Connector key format is not supported by this client.");

            var encryptedUserKey = EncString.Parse(wrappedUserKey);
            if (encryptedUserKey.Type != EncryptionType.AesCbc256_HmacSha256_B64)
                throw new InvalidDataException(
                    "This Key Connector user-key format is not supported by this client.");

            using var stretchedKey = _crypto.StretchMasterKey(keyConnectorKey);
            try
            {
                return _crypto.DecryptUserKey(stretchedKey, encryptedUserKey);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException(
                    "This Key Connector user-key format is not supported by this client.",
                    ex);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyConnectorKey);
        }
    }

    private bool IsRemovePinUnlockPolicyEnabled() =>
        VaultPolicyEvaluator.IsEnabledForUser(
            _session,
            OrganizationPolicyType.RemoveUnlockWithPin);

    private AuthResult RevokePinUnlock(VaultSessionLease lease)
    {
        try
        {
            if (!_session.TryCommit(lease, () =>
            {
                if (!_tokenStore.TryLoad(out var persisted)
                    || persisted.AccountId != lease.AccountId
                    || persisted.Endpoints != lease.Endpoints
                    || string.IsNullOrEmpty(persisted.PinProtectedUserKey))
                    return;

                _tokenStore.Save(persisted with
                {
                    Generation = lease.Generation,
                    PinProtectedUserKey = null,
                    PinFailedAttempts = 0,
                });
            }))
            {
                return SessionExpiredFailure();
            }
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            _session.TryLockIfCurrent(lease);
            return SafeFailure(
                "PIN unlock is disabled by organization policy, but the saved PIN could not be removed securely.");
        }

        _session.TryLockIfCurrent(lease);
        return new AuthResult.PinCleared(
            "PIN unlock was disabled by organization policy. Use the master password to unlock.");
    }

    private static AuthResult.Failure InvalidResponseFailure() =>
        new("The authentication response was invalid.", AuthFailureKind.InvalidResponse);

    private static AuthResult.Failure SessionExpiredFailure() =>
        new("The authentication session expired.", AuthFailureKind.SessionExpired);

    private static AuthResult.Failure SafeFailure(string message) =>
        new(message, AuthFailureKind.SafeMessage);

    private static AuthResult.Failure AuthenticationExpired() => SessionExpiredFailure();

    private string NewDeviceIdentifier() =>
        _deviceIdentifierProvider?.GetDeviceIdentifier()
        ?? Guid.NewGuid().ToString("D");

    private bool TryGetStableDeviceIdentifier(out string deviceIdentifier)
    {
        deviceIdentifier = string.Empty;
        if (_deviceIdentifierProvider is null)
            return false;

        try
        {
            var value = _deviceIdentifierProvider.GetDeviceIdentifier();
            if (!Guid.TryParseExact(value, "D", out var parsed)
                || parsed == Guid.Empty)
            {
                return false;
            }
            deviceIdentifier = parsed.ToString("D");
            return true;
        }
        catch (Exception ex) when (ex is
            IOException
                or UnauthorizedAccessException
                or CryptographicException
                or InvalidDataException
                or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static void ValidateSsoAuthorization(SsoAuthorizationCode authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization.Code)
            || authorization.Code.Length > 4096
            || string.IsNullOrEmpty(authorization.CodeVerifier)
            || authorization.CodeVerifier.Length is < 43 or > 128
            || authorization.CodeVerifier.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~'))
            || string.IsNullOrEmpty(authorization.State)
            || authorization.State.Length is < 43 or > 512
            || !Uri.TryCreate(authorization.RedirectUri, UriKind.Absolute, out var redirect)
            || !redirect.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !redirect.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || redirect.Port is < AppSsoPorts.First or > AppSsoPorts.Last
            || redirect.AbsolutePath != "/"
            || !string.IsNullOrEmpty(redirect.Query)
            || !string.IsNullOrEmpty(redirect.Fragment)
            || !string.IsNullOrEmpty(redirect.UserInfo))
        {
            throw new ArgumentException("The SSO authorization response is invalid.", nameof(authorization));
        }
    }

    private static string NormalizeServerUrl(string serverUrl) => serverUrl.TrimEnd('/');

    private static string? NormalizeOptionalUserKeyId(string? value)
    {
        if (value is null)
            return null;
        if (value.Length != 32
            || value.Any(static c => c is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                "The contained user-key identifier is not canonical lowercase hexadecimal.");
        }

        return value;
    }

    private static bool IsSupportedTrustedDeviceEnvelope(
        string encryptedPrivateKey,
        string encryptedUserKey)
    {
        try
        {
            var privateKey = EncString.Parse(encryptedPrivateKey);
            var userKey = EncString.Parse(encryptedUserKey);
            return privateKey.Type == EncryptionType.AesCbc256_HmacSha256_B64
                && userKey.Type.IsRsaEnvelope();
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsSupportedLegacyKeyConnectorAccountShape(TokenResponse token)
    {
        return IsType2EncString(token.Key) && IsSupportedLegacyAccountKeyShape(token);
    }

    private static AccountKeyKind ClassifyAccountKeys(TokenResponse token)
    {
        if (IsSupportedLegacyAccountKeyShape(token))
            return AccountKeyKind.LegacyV1;
        if (token.AccountKeys is not
            {
                PublicKeyEncryptionKeyPair:
                {
                    WrappedPrivateKey: { } wrappedPrivateKey,
                },
                SignatureKeyPair:
                {
                    WrappedSigningKey: { } wrappedSigningKey,
                },
                SecurityState.SecurityState: { } securityState,
                SecurityState.SecurityVersion: V2AccountKeysValidator.CurrentSecurityVersion,
            }
            || string.IsNullOrWhiteSpace(wrappedPrivateKey)
            || string.IsNullOrWhiteSpace(wrappedSigningKey)
            || string.IsNullOrWhiteSpace(securityState))
        {
            return AccountKeyKind.Invalid;
        }

        try
        {
            var privateKey = CoseEncrypt0Codec.ParseEncString(wrappedPrivateKey);
            var signingKey = CoseEncrypt0Codec.ParseEncString(wrappedSigningKey);
            return privateKey.ContentFormat == CoseContentFormat.Pkcs8PrivateKey
                && signingKey.ContentFormat == CoseContentFormat.CoseKey
                && privateKey.KeyId.Span.SequenceEqual(signingKey.KeyId.Span)
                ? AccountKeyKind.V2
                : AccountKeyKind.Invalid;
        }
        catch (Exception ex) when (ex is
            CborContentException or FormatException or CryptographicException)
        {
            return AccountKeyKind.Invalid;
        }
    }

    private static PersistedAccountCryptographicState? CaptureAccountCryptographicState(
        TokenResponse token,
        AccountKeyKind accountKeyKind)
    {
        switch (accountKeyKind)
        {
            case AccountKeyKind.LegacyV1:
            {
                var accountKeysPrivateKey = token.AccountKeys?
                    .PublicKeyEncryptionKeyPair?
                    .WrappedPrivateKey;
                if (!string.IsNullOrWhiteSpace(accountKeysPrivateKey)
                    && !string.IsNullOrWhiteSpace(token.PrivateKey)
                    && !string.Equals(
                        accountKeysPrivateKey,
                        token.PrivateKey,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The response contains conflicting wrapped account private keys.");
                }

                var wrappedPrivateKey = accountKeysPrivateKey ?? token.PrivateKey;
                return string.IsNullOrWhiteSpace(wrappedPrivateKey)
                    ? null
                    : new PersistedAccountCryptographicState(
                        1,
                        wrappedPrivateKey);
            }

            case AccountKeyKind.V2 when token.AccountKeys is
            {
                PublicKeyEncryptionKeyPair: { } encryptionPair,
                SignatureKeyPair: { } signaturePair,
                SecurityState: { } securityState,
            }:
            {
                var state = new PersistedAccountCryptographicState(
                    securityState.SecurityVersion,
                    encryptionPair.WrappedPrivateKey!,
                    encryptionPair.PublicKey,
                    encryptionPair.SignedPublicKey,
                    signaturePair.WrappedSigningKey,
                    signaturePair.VerifyingKey,
                    securityState.SecurityState);
                return state.HasValidShape
                    ? state
                    : throw new InvalidDataException(
                        "The V2 account cryptographic state has an invalid persisted shape.");
            }

            default:
                throw new InvalidDataException(
                    "The account cryptographic state is not supported.");
        }
    }

    private static AccountKeysResponse RestoreAccountKeysResponse(
        PersistedAccountCryptographicState state)
    {
        if (state.SecurityVersion != 2 || !state.HasValidShape)
            throw new InvalidDataException("The saved V2 account cryptographic state is invalid.");

        return new AccountKeysResponse(
            new PublicKeyEncryptionKeyPairResponse(
                state.WrappedPrivateKey,
                state.PublicKey,
                state.SignedPublicKey),
            new SignatureKeyPairResponse(
                state.WrappedSigningKey,
                state.VerifyingKey),
            new AccountSecurityState(
                state.SignedSecurityState,
                state.SecurityVersion));
    }

    private static void ValidateV2UserKeyIdAgainstAccountCryptographicState(
        string? userKeyId,
        PersistedAccountCryptographicState state)
    {
        if (userKeyId is null)
            return;
        if (state.SecurityVersion != 2
            || !string.Equals(
                CoseEncrypt0Codec.ParseEncString(state.WrappedPrivateKey).KeyIdHex,
                userKeyId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The contained user-key identifier does not match the V2 account state.");
        }
    }

    private VaultUserKey UnwrapUserKey(
        SymmetricCryptoKey stretchedKey,
        string protectedUserKey,
        int securityVersion,
        string? expectedUserKeyId)
    {
        byte[]? encodedKey = _crypto.DecryptUserKeyMaterial(
            stretchedKey,
            EncString.Parse(protectedUserKey));
        VaultUserKey? userKey = null;
        try
        {
            userKey = VaultUserKey.ImportOwnedTransport(encodedKey, securityVersion);
            encodedKey = null;
            if (securityVersion == 2
                && expectedUserKeyId is not null
                && !string.Equals(
                    expectedUserKeyId,
                    userKey.KeyId,
                    StringComparison.Ordinal))
            {
                throw new CryptographicException(
                    "The decrypted V2 user key does not match its contained key identifier.");
            }

            var result = userKey;
            userKey = null;
            return result;
        }
        finally
        {
            if (encodedKey is not null)
                CryptographicOperations.ZeroMemory(encodedKey);
            userKey?.Dispose();
        }
    }

    private string? ValidateAccountCryptographicState(
        VaultUserKey userKey,
        PersistedAccountCryptographicState? state)
    {
        ArgumentNullException.ThrowIfNull(userKey);
        if (userKey.SecurityVersion == 1)
        {
            if (state is null)
                return null;
            if (state.SecurityVersion != 1
                || !state.HasValidShape
                || !IsType2EncString(state.WrappedPrivateKey))
            {
                throw new InvalidDataException(
                    "The V1 account cryptographic state is invalid.");
            }

            return state.WrappedPrivateKey;
        }

        if (state is null || state.SecurityVersion != 2)
            throw new InvalidDataException("The V2 account cryptographic state is missing.");

        return _v2AccountKeysValidator
            .Validate(RestoreAccountKeysResponse(state), userKey.V2!)
            .WrappedPrivateKey;
    }

    private static bool IsTdeV2JitCandidate(
        PendingSsoTwoFactorContext context,
        TokenResponse token,
        UserDecryptionOptionsResponse? decryptionOptions,
        TrustedDeviceUserDecryptionOptionResponse? trustedDevice,
        KeyConnectorUserDecryptionOptionResponse? keyConnector) =>
        decryptionOptions is { HasMasterPassword: false }
        && IsActiveTrustedDeviceOption(trustedDevice)
        && trustedDevice!.HasAdminApproval == false
        && trustedDevice!.HasLoginApprovingDevice == false
        && string.IsNullOrWhiteSpace(keyConnector?.KeyConnectorUrl)
        && string.IsNullOrWhiteSpace(trustedDevice.EncryptedPrivateKey)
        && string.IsNullOrWhiteSpace(trustedDevice.EncryptedUserKey)
        && string.IsNullOrWhiteSpace(token.Key)
        && string.IsNullOrWhiteSpace(token.PrivateKey)
        && token.AccountKeys is null
        && !string.IsNullOrWhiteSpace(context.OrganizationIdentifier);

    private static bool IsActiveTrustedDeviceOption(
        TrustedDeviceUserDecryptionOptionResponse? trustedDevice) => trustedDevice is
    {
        IsTdeOffboarding: false,
        HasAdminApproval: not null,
        HasLoginApprovingDevice: not null,
        HasManageResetPasswordPermission: not null,
    };

    private static bool IsSupportedLegacyAccountKeyShape(TokenResponse token)
    {
        if (!string.IsNullOrWhiteSpace(token.PrivateKey)
            && !IsType2EncString(token.PrivateKey))
        {
            return false;
        }

        if (token.AccountKeys is not { } accountKeys)
            return true;
        if (accountKeys.SignatureKeyPair is not null
            || accountKeys.SecurityState is not null
            || accountKeys.PublicKeyEncryptionKeyPair is not { } keyPair
            || !string.IsNullOrWhiteSpace(keyPair.SignedPublicKey))
        {
            return false;
        }

        return IsType2EncString(keyPair.WrappedPrivateKey);
    }

    private static bool IsType2EncString(string? value)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(value)
                && EncString.Parse(value).Type == EncryptionType.AesCbc256_HmacSha256_B64;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private bool TryRemovePersistedAccountData(
        string accountId,
        ServerEndpoints endpoints)
    {
        var removed = true;

        try
        {
            removed = _tokenStore.Remove(accountId, endpoints);
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            removed = false;
        }

        try
        {
            _cache.Clear(accountId, endpoints);
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            removed = false;
        }

        return removed;
    }

    private static bool IsExpectedPersistenceException(Exception ex) => ex is
        IOException
            or UnauthorizedAccessException
            or CryptographicException
            or InvalidDataException
            or JsonException
            or PlatformNotSupportedException;

    private static bool IsExpectedAuthException(Exception ex) =>
        ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or CborContentException
            or CryptographicException
            or FormatException
            or ArgumentException
            or InvalidDataException
            or NotSupportedException
            or NotImplementedException;

    private sealed record LoginWithDeviceBinding(
        AuthenticationAttemptLease Attempt,
        ServerEndpoints Endpoints,
        string Email,
        string DeviceIdentifier,
        LoginWithDeviceQuarantineBinding QuarantineBinding,
        string RequestId,
        string FingerprintPhrase,
        DateTimeOffset ExpiresAtUtc);

    private sealed class PendingLoginWithDeviceContext(
        AuthenticationAttemptLease attempt,
        ServerEndpoints endpoints,
        string email,
        string deviceIdentifier,
        LoginWithDeviceQuarantineBinding quarantineBinding,
        string requestId,
        string fingerprintPhrase,
        DateTimeOffset expiresAtUtc) : PendingLoginWithDeviceOperationContext
    {
        public LoginWithDeviceBinding Binding { get; } = new(
            attempt,
            endpoints,
            email,
            deviceIdentifier,
            quarantineBinding,
            requestId,
            fingerprintPhrase,
            expiresAtUtc);

        protected override void DisposeSecrets()
        {
        }
    }

    private sealed class PendingLoginWithDeviceTwoFactorContext(
        LoginWithDeviceBinding binding,
        LoginWithDeviceGrant grant,
        LoginWithDeviceQuarantineRecord quarantineRecord,
        IReadOnlyList<int> providers,
        IReadOnlyDictionary<int, string> providerData)
        : PendingLoginWithDeviceOperationContext(quarantineRecord)
    {
        public LoginWithDeviceBinding Binding { get; } = binding;
        public LoginWithDeviceGrant Grant { get; } = grant;
        public IReadOnlyList<int> Providers { get; private set; } = providers;
        public IReadOnlyDictionary<int, string> ProviderData { get; private set; } =
            providerData;

        public void UpdateChallenge(
            IReadOnlyList<int> providers,
            IReadOnlyDictionary<int, string> providerData)
        {
            Providers = providers.ToArray();
            ProviderData = providerData;
        }

        protected override void DisposeSecrets() => Grant.Dispose();
    }

    private abstract class PendingLoginWithDeviceOperationContext : IDisposable
    {
        private readonly object _lifetimeGate = new();
        private readonly object _revocationGate = new();
        private LoginWithDeviceQuarantineRecord? _quarantineRecord;
        private bool _disposeRequested;
        private bool _expirationRequested;
        private bool _expiryDisarmed;
        private bool _operationActive;
        private bool _secretsDisposed;
        private bool _identityExchangeStarted;
        private VaultSessionLease? _promotedLease;
        private ITimer? _expiryTimer;
        private bool _revocationCompleted;
        private bool _revocationSucceeded;

        protected PendingLoginWithDeviceOperationContext(
            LoginWithDeviceQuarantineRecord? quarantineRecord = null)
        {
            _quarantineRecord = quarantineRecord;
        }

        public LoginWithDeviceQuarantineRecord? QuarantineRecord
        {
            get
            {
                lock (_lifetimeGate)
                    return _quarantineRecord;
            }
        }

        public void SetQuarantineRecord(LoginWithDeviceQuarantineRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            lock (_lifetimeGate)
            {
                if (_quarantineRecord is not null
                    && _quarantineRecord != record)
                {
                    throw new InvalidOperationException(
                        "The login-with-device operation is already bound to another quarantine record.");
                }
                _quarantineRecord = record;
            }
        }

        public bool DisposeRequested
        {
            get
            {
                lock (_lifetimeGate)
                    return _disposeRequested;
            }
        }

        public bool TryAcquireOperation(out IDisposable operation)
        {
            lock (_lifetimeGate)
            {
                if (_disposeRequested || _operationActive)
                {
                    operation = null!;
                    return false;
                }

                _operationActive = true;
                operation = new LoginWithDeviceOperation(this);
                return true;
            }
        }

        public bool IdentityExchangeStarted
        {
            get
            {
                lock (_lifetimeGate)
                    return _identityExchangeStarted;
            }
        }

        public VaultSessionLease? PromotedLease
        {
            get
            {
                lock (_lifetimeGate)
                    return _promotedLease;
            }
        }

        public bool TryMarkIdentityExchangeStarted()
        {
            lock (_lifetimeGate)
            {
                if (_disposeRequested || !_operationActive)
                    return false;
                _identityExchangeStarted = true;
                return true;
            }
        }

        public void ClearIdentityExchangeStarted()
        {
            lock (_lifetimeGate)
                _identityExchangeStarted = false;
        }

        public bool TrySetPromotedLease(VaultSessionLease lease)
        {
            ArgumentNullException.ThrowIfNull(lease);
            lock (_lifetimeGate)
            {
                if (_disposeRequested)
                    return false;
                _promotedLease = lease;
                return true;
            }
        }

        public bool TryRequestExpiry(
            DateTimeOffset now,
            DateTimeOffset expiresAt)
        {
            lock (_lifetimeGate)
            {
                if (_expiryDisarmed || now < expiresAt)
                    return false;
                _expirationRequested = true;
                return true;
            }
        }

        public bool TryFinalizeCommittedPromotion(
            DateTimeOffset now,
            DateTimeOffset expiresAt)
        {
            ITimer? timer = null;
            lock (_lifetimeGate)
            {
                if (now >= expiresAt)
                {
                    _expirationRequested = true;
                    _disposeRequested = true;
                }
                if (_disposeRequested || _expirationRequested)
                    return false;

                _expiryDisarmed = true;
                timer = _expiryTimer;
                _expiryTimer = null;
            }
            timer?.Dispose();
            return true;
        }

        public bool RevokePromotedSession(
            Func<VaultSessionLease, bool> revoke)
        {
            ArgumentNullException.ThrowIfNull(revoke);
            lock (_revocationGate)
            {
                if (_revocationCompleted)
                    return _revocationSucceeded;

                var lease = PromotedLease;
                if (lease is null)
                    return true;

                _revocationSucceeded = revoke(lease);
                _revocationCompleted = true;
                return _revocationSucceeded;
            }
        }

        public void ArmExpiryTimer(
            TimeProvider timeProvider,
            TimeSpan dueTime,
            Action callback)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentNullException.ThrowIfNull(callback);
            var timer = timeProvider.CreateTimer(
                static state => ((Action)state!).Invoke(),
                callback,
                dueTime > TimeSpan.Zero ? dueTime : TimeSpan.Zero,
                Timeout.InfiniteTimeSpan);
            ITimer? prior = null;
            var retain = false;
            lock (_lifetimeGate)
            {
                if (!_disposeRequested && !_secretsDisposed && !_expiryDisarmed)
                {
                    prior = _expiryTimer;
                    _expiryTimer = timer;
                    retain = true;
                }
            }
            prior?.Dispose();
            if (!retain)
                timer.Dispose();
        }

        public void Dispose()
        {
            var disposeSecrets = false;
            ITimer? timer = null;
            lock (_lifetimeGate)
            {
                _disposeRequested = true;
                if (!_operationActive && !_secretsDisposed)
                {
                    _secretsDisposed = true;
                    timer = _expiryTimer;
                    _expiryTimer = null;
                    disposeSecrets = true;
                }
            }
            timer?.Dispose();
            if (disposeSecrets)
                DisposeSecrets();
        }

        protected abstract void DisposeSecrets();

        private void ReleaseOperation()
        {
            var disposeSecrets = false;
            ITimer? timer = null;
            lock (_lifetimeGate)
            {
                _operationActive = false;
                if (_disposeRequested && !_secretsDisposed)
                {
                    _secretsDisposed = true;
                    timer = _expiryTimer;
                    _expiryTimer = null;
                    disposeSecrets = true;
                }
            }
            timer?.Dispose();
            if (disposeSecrets)
                DisposeSecrets();
        }

        private sealed class LoginWithDeviceOperation(
            PendingLoginWithDeviceOperationContext owner) : IDisposable
        {
            private PendingLoginWithDeviceOperationContext? _owner = owner;

            public void Dispose() =>
                Interlocked.Exchange(ref _owner, null)?.ReleaseOperation();
        }
    }

    private sealed record PendingTwoFactorContext(
        AuthenticationAttemptLease Attempt,
        ServerEndpoints Endpoints,
        string Email,
        string DeviceIdentifier,
        string PasswordHash,
        char[] MasterPassword,
        SymmetricCryptoKey StretchedKey,
        KdfType KdfType,
        int KdfIterations,
        int? KdfMemory,
        int? KdfParallelism,
        string MasterPasswordSalt,
        MasterPasswordEvidence PasswordEvidence,
        IReadOnlyList<int> Providers,
        IReadOnlyDictionary<int, string> ProviderData,
        bool RequiresDeviceVerification,
        DateTimeOffset? DeviceVerificationExpiresAt);

    private sealed record PendingSsoTwoFactorContext(
        AuthenticationAttemptLease Attempt,
        ServerEndpoints Endpoints,
        string Email,
        string DeviceIdentifier,
        string Code,
        string CodeVerifier,
        string RedirectUri,
        string? OrganizationIdentifier,
        IReadOnlyList<int> Providers,
        IReadOnlyDictionary<int, string> ProviderData,
        string? SsoEmail2FaSessionToken,
        string? ChallengeEmail,
        string? AuthenticatedEmail = null);

    private sealed record MasterPasswordUnlockMaterial(
        string ProtectedUserKey,
        KdfType KdfType,
        int KdfIterations,
        int? KdfMemory,
        int? KdfParallelism,
        string MasterPasswordSalt,
        string? UserKeyId);

    private sealed record PendingTrustedDeviceAccountState(
        VaultSessionLease Lease,
        AccountKeyKind AccountKeyKind,
        AccountKeysResponse? AccountKeys,
        string? UserKeyId);

    private sealed record PasswordChangeRequirement(
        RequiredPasswordChangeReason Reason,
        MasterPasswordPolicyRequirements? Policy);

    private sealed class PendingRequiredPasswordChangeContext : IDisposable
    {
        private readonly object _lifetimeGate = new();
        private bool _disposeRequested;
        private bool _operationActive;
        private bool _keyDisposed;

        public PendingRequiredPasswordChangeContext(
            VaultSessionLease lease,
            string email,
            string currentPasswordHash,
            KdfType kdfType,
            int kdfIterations,
            int? kdfMemory,
            int? kdfParallelism,
            string masterPasswordSalt,
            RequiredPasswordChangeReason reason,
            MasterPasswordPolicyRequirements? policy,
            SymmetricCryptoKey userKey)
        {
            Lease = lease;
            Email = email;
            CurrentPasswordHash = currentPasswordHash;
            KdfType = kdfType;
            KdfIterations = kdfIterations;
            KdfMemory = kdfMemory;
            KdfParallelism = kdfParallelism;
            MasterPasswordSalt = masterPasswordSalt;
            Reason = reason;
            Policy = policy;
            UserKey = userKey;
        }

        public VaultSessionLease Lease { get; }
        public string Email { get; }
        public string CurrentPasswordHash { get; }
        public KdfType KdfType { get; }
        public int KdfIterations { get; }
        public int? KdfMemory { get; }
        public int? KdfParallelism { get; }
        public string MasterPasswordSalt { get; }
        public RequiredPasswordChangeReason Reason { get; }
        public MasterPasswordPolicyRequirements? Policy { get; }
        public SymmetricCryptoKey UserKey { get; }

        public bool TryAcquireOperation(out IDisposable operation)
        {
            lock (_lifetimeGate)
            {
                if (_disposeRequested || _operationActive)
                {
                    operation = null!;
                    return false;
                }

                _operationActive = true;
                operation = new RequiredPasswordChangeOperation(this);
                return true;
            }
        }

        public void Dispose()
        {
            var disposeKey = false;
            lock (_lifetimeGate)
            {
                _disposeRequested = true;
                if (!_operationActive && !_keyDisposed)
                {
                    _keyDisposed = true;
                    disposeKey = true;
                }
            }
            if (disposeKey)
                UserKey.Dispose();
        }

        private void ReleaseOperation()
        {
            var disposeKey = false;
            lock (_lifetimeGate)
            {
                _operationActive = false;
                if (_disposeRequested && !_keyDisposed)
                {
                    _keyDisposed = true;
                    disposeKey = true;
                }
            }
            if (disposeKey)
                UserKey.Dispose();
        }

        private sealed class RequiredPasswordChangeOperation(
            PendingRequiredPasswordChangeContext owner) : IDisposable
        {
            private PendingRequiredPasswordChangeContext? _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseOperation();
        }
    }

    private enum AccountKeyKind
    {
        Invalid,
        LegacyV1,
        V2,
    }

    private static class AppSsoPorts
    {
        public const int First = 8065;
        public const int Last = 8070;
    }
}
