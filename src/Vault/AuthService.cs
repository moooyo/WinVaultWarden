using System.Security.Cryptography;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

public sealed class AuthService : IAuthService
{
    private const string DeviceName = "WinVaultWarden";

    private readonly IReadonlyApiClient _api;
    private readonly CryptoService _crypto;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;
    private readonly VaultBootstrapper _bootstrapper;
    private PendingTwoFactorContext? _pendingTwoFactor;

    public AuthService(
        IReadonlyApiClient api,
        CryptoService crypto,
        VaultSession session,
        ITokenStore tokenStore,
        VaultBootstrapper bootstrapper)
    {
        _api = api;
        _crypto = crypto;
        _session = session;
        _tokenStore = tokenStore;
        _bootstrapper = bootstrapper;
    }

    public async Task<AuthResult> LoginAsync(
        string serverUrl,
        string email,
        string masterPassword,
        CancellationToken ct = default)
    {
        try
        {
            var normalizedServerUrl = NormalizeServerUrl(serverUrl);
            _api.SetBaseAddress(normalizedServerUrl);
            _session.SetState(Core.Session.VaultState.Unlocking);

            var prelogin = await _api.PreloginAsync(email, ct);

            var context = BuildLoginContext(normalizedServerUrl, email, masterPassword, prelogin, NewDeviceIdentifier());
            var result = await _api.ConnectTokenAsync(ConnectTokenRequest.Password(
                email,
                context.PasswordHash,
                context.DeviceIdentifier,
                DeviceName,
                null), ct);

            return await HandleLoginResultAsync(context, result, ct);
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            _session.SetState(Core.Session.VaultState.Error);
            return new AuthResult.Failure(ex.Message);
        }
    }

    public async Task<AuthResult> SubmitTwoFactorAsync(string code, CancellationToken ct = default, bool rememberDevice = true)
    {
        if (_pendingTwoFactor is not { } context)
            return new AuthResult.Failure("No two-factor challenge is pending.");

        try
        {
            var provider = context.Providers.FirstOrDefault();
            _api.SetBaseAddress(context.ServerUrl);
            var result = await _api.ConnectTokenAsync(ConnectTokenRequest.Password(
                context.Email,
                context.PasswordHash,
                context.DeviceIdentifier,
                DeviceName,
                new TwoFactorPayload(provider, code, rememberDevice)), ct);

            return await HandleLoginResultAsync(context with { Providers = Array.Empty<int>() }, result, ct);
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            _session.SetState(Core.Session.VaultState.Error);
            return new AuthResult.Failure(ex.Message);
        }
    }

    public async Task<AuthResult> UnlockAsync(string masterPassword, CancellationToken ct = default)
    {
        if (!_tokenStore.TryLoad(out var persisted))
            return new AuthResult.Failure("No saved session.");

        try
        {
            _api.SetBaseAddress(persisted.ServerUrl);
            _session.SetState(Core.Session.VaultState.Unlocking);

            var stretchedKey = DeriveStretchedKey(
                masterPassword,
                persisted.Email,
                persisted.KdfType,
                persisted.KdfIterations,
                persisted.KdfMemory,
                persisted.KdfParallelism);
            var userKey = _crypto.DecryptUserKey(stretchedKey, EncString.Parse(persisted.ProtectedUserKey));

            var refresh = await _api.ConnectTokenAsync(ConnectTokenRequest.Refresh(
                persisted.RefreshToken,
                persisted.DeviceIdentifier,
                DeviceName), ct);

            if (refresh is not ConnectTokenResult.Success success)
            {
                _session.Lock();
                return ToFailure(refresh);
            }

            _session.SetTokens(success.Token.AccessToken, success.Token.RefreshToken);
            _session.SetUnlockedKey(userKey);
            _tokenStore.Save(persisted with { RefreshToken = success.Token.RefreshToken });
            await _bootstrapper.BootstrapAsync(persisted.ServerUrl, ct);
            return new AuthResult.Success();
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            _session.Lock();
            return new AuthResult.Failure(ex.Message);
        }
    }

    public async Task<AuthResult> UnlockWithPinAsync(string pin, CancellationToken ct = default)
    {
        if (!_tokenStore.TryLoad(out var persisted) || string.IsNullOrEmpty(persisted.PinProtectedUserKey))
            return new AuthResult.Failure("未设置 PIN。");

        SymmetricCryptoKey userKey;
        try
        {
            _api.SetBaseAddress(persisted.ServerUrl);
            _session.SetState(Core.Session.VaultState.Unlocking);

            var stretchedKey = DeriveStretchedKey(
                pin, persisted.Email, persisted.KdfType,
                persisted.KdfIterations, persisted.KdfMemory, persisted.KdfParallelism);
            try
            {
                userKey = _crypto.DecryptUserKey(stretchedKey, EncString.Parse(persisted.PinProtectedUserKey));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                _session.Lock();
                return HandleWrongPin(persisted);
            }

            var refresh = await _api.ConnectTokenAsync(ConnectTokenRequest.Refresh(
                persisted.RefreshToken, persisted.DeviceIdentifier, DeviceName), ct);

            if (refresh is not ConnectTokenResult.Success success)
            {
                _session.Lock();
                return ToFailure(refresh);
            }

            _session.SetTokens(success.Token.AccessToken, success.Token.RefreshToken);
            _session.SetUnlockedKey(userKey);
            _tokenStore.Save(persisted with { RefreshToken = success.Token.RefreshToken, PinFailedAttempts = 0 });
            await _bootstrapper.BootstrapAsync(persisted.ServerUrl, ct);
            return new AuthResult.Success();
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            _session.Lock();
            return new AuthResult.Failure(ex.Message);
        }
    }

    private AuthResult HandleWrongPin(PersistedSession persisted)
    {
        var n = persisted.PinFailedAttempts + 1;
        if (n >= 5)
        {
            _tokenStore.Save(persisted with { PinProtectedUserKey = null, PinFailedAttempts = 0 });
            return new AuthResult.PinCleared("PIN 已因多次错误被清除，请用主密码解锁。");
        }
        _tokenStore.Save(persisted with { PinFailedAttempts = n });
        return new AuthResult.Failure($"PIN 错误，还可尝试 {5 - n} 次。");
    }

    public Task LockAsync(CancellationToken ct = default)
    {
        _session.Lock();
        return Task.CompletedTask;
    }

    public Task LogoutAsync(CancellationToken ct = default)
    {
        _tokenStore.Clear();
        _session.Clear();
        _pendingTwoFactor = null;
        return Task.CompletedTask;
    }

    private async Task<AuthResult> HandleLoginResultAsync(
        PendingTwoFactorContext context,
        ConnectTokenResult result,
        CancellationToken ct)
    {
        switch (result)
        {
            case ConnectTokenResult.Success success:
                _pendingTwoFactor = null;
                return await CompleteLoginAsync(context, success.Token, ct);
            case ConnectTokenResult.TwoFactorRequired twoFactor:
                _pendingTwoFactor = context with { Providers = twoFactor.Providers.ToArray() };
                return new AuthResult.TwoFactorRequired(twoFactor.Providers);
            default:
                return ToFailure(result);
        }
    }

    private async Task<AuthResult> CompleteLoginAsync(
        PendingTwoFactorContext context,
        TokenResponse token,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token.Key))
            return new AuthResult.Failure("Token response did not include a protected user key.");

        var userKey = _crypto.DecryptUserKey(context.StretchedKey, EncString.Parse(token.Key));
        _session.SetTokens(token.AccessToken, token.RefreshToken);
        _session.SetUnlockedKey(userKey);
        _tokenStore.Save(new PersistedSession(
            context.ServerUrl,
            context.Email,
            context.DeviceIdentifier,
            token.RefreshToken,
            token.Key,
            context.KdfType,
            context.KdfIterations,
            context.KdfMemory,
            context.KdfParallelism));
        await _bootstrapper.BootstrapAsync(context.ServerUrl, ct);
        return new AuthResult.Success();
    }

    private PendingTwoFactorContext BuildLoginContext(
        string serverUrl,
        string email,
        string masterPassword,
        PreloginResponse prelogin,
        string deviceIdentifier)
    {
        var masterKey = _crypto.DeriveMasterKey(
            masterPassword,
            email,
            prelogin.Kdf,
            prelogin.KdfIterations,
            prelogin.KdfMemory,
            prelogin.KdfParallelism);
        try
        {
            var passwordHash = _crypto.ComputeMasterPasswordHash(masterKey, masterPassword);
            var stretchedKey = _crypto.StretchMasterKey(masterKey);
            return new PendingTwoFactorContext(
                serverUrl,
                email,
                deviceIdentifier,
                passwordHash,
                stretchedKey,
                prelogin.Kdf,
                prelogin.KdfIterations,
                prelogin.KdfMemory,
                prelogin.KdfParallelism,
                Array.Empty<int>());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private SymmetricCryptoKey DeriveStretchedKey(
        string masterPassword,
        string email,
        KdfType kdfType,
        int iterations,
        int? memory,
        int? parallelism)
    {
        var masterKey = _crypto.DeriveMasterKey(masterPassword, email, kdfType, iterations, memory, parallelism);
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
        ConnectTokenResult.Error error => new AuthResult.Failure(error.Message),
        ConnectTokenResult.TwoFactorRequired => new AuthResult.Failure("Two-factor authentication is still required."),
        _ => new AuthResult.Failure("Authentication failed."),
    };

    private static string NewDeviceIdentifier() => Guid.NewGuid().ToString("D");

    private static string NormalizeServerUrl(string serverUrl) => serverUrl.TrimEnd('/');

    private static bool IsExpectedAuthException(Exception ex) =>
        ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or CryptographicException
            or FormatException
            or ArgumentException
            or NotSupportedException
            or NotImplementedException;

    private sealed record PendingTwoFactorContext(
        string ServerUrl,
        string Email,
        string DeviceIdentifier,
        string PasswordHash,
        SymmetricCryptoKey StretchedKey,
        KdfType KdfType,
        int KdfIterations,
        int? KdfMemory,
        int? KdfParallelism,
        IReadOnlyList<int> Providers);
}
