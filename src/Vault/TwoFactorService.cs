using System.Security.Cryptography;
using System.Net;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Passkeys;
using Core.Services;
using Crypto;

namespace Vault;

/// <summary>
/// Coordinates account-level two-factor provider management.
/// <para>
/// Password-backed operations derive the master-password hash from the persisted KDF settings
/// and clear the temporary master key. WebAuthn management additionally supports the mutually
/// exclusive protected-action OTP and auth-request access-code proofs accepted by compatible servers.
/// </para>
/// <para>
/// These operations do not require the vault user key because the server verifies the selected
/// proof on the account-level two-factor endpoints.
/// </para>
/// </summary>
public sealed class TwoFactorService : ITwoFactorService
{
    private const int MaxWebAuthnCredentialNameLength = 256;

    private readonly CryptoService _crypto;
    private readonly IVaultApiClientProvider _clients;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;

    public TwoFactorService(
        CryptoService crypto,
        IVaultApiClientProvider clients,
        VaultSession session,
        ITokenStore tokenStore)
    {
        _crypto = crypto;
        _clients = clients;
        _session = session;
        _tokenStore = tokenStore;
    }

    // -----------------------------------------------------------------------
    // ListProvidersAsync — 不需要主密码，直接拉取提供者列表
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<TwoFactorProvider>> ListProvidersAsync(CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        var response = await api.GetProvidersAsync(linked.Token);
        _session.ThrowIfExpired(lease);
        return response.Data
            .Select(item => new TwoFactorProvider(item.Type, item.Enabled))
            .ToList()
            .AsReadOnly();
    }

    // -----------------------------------------------------------------------
    // BeginTotpSetupAsync — 拿到服务端已存储的（或新生成的）TOTP 密钥，构造 otpauth URI
    // -----------------------------------------------------------------------

    public async Task<(string secret, string otpauth)> BeginTotpSetupAsync(
        string pw, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        var persisted = LoadPersisted(lease);
        var mphash = ComputeHash(pw, persisted);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);
        var response = await api.GetAuthenticatorAsync(new PasswordVerifyRequest(mphash), linked.Token);
        _session.ThrowIfExpired(lease);

        var secret = response.Key;
        var otpauth = $"otpauth://totp/{Uri.EscapeDataString(persisted.Email)}?secret={Uri.EscapeDataString(secret)}&issuer=WinVaultWarden";
        return (secret, otpauth);
    }

    // -----------------------------------------------------------------------
    // EnableTotpAsync — 把 secret(=key) + code(=token) + hash 发给服务端启用，再拉 recovery code
    // -----------------------------------------------------------------------

    public async Task<TotpActivationResult> EnableTotpAsync(
        string pw, string secret, string code, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        var persisted = LoadPersisted(lease);
        var mphash = ComputeHash(pw, persisted);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);
        var enabled = await api.EnableAuthenticatorAsync(
            new EnableAuthenticatorRequest(Key: secret, Token: code, MasterPasswordHash: mphash), linked.Token);
        if (!enabled.Enabled)
            throw new TwoFactorOperationException("服务器未确认 TOTP 已启用，请稍后重试。");

        try
        {
            _session.ThrowIfExpired(lease);
            var recover = await api.GetRecoverAsync(new PasswordVerifyRequest(mphash), linked.Token);
            _session.ThrowIfExpired(lease);
            return string.IsNullOrWhiteSpace(recover.Code)
                ? TotpActivationResult.EnabledWithoutRecoveryCode()
                : TotpActivationResult.EnabledWithRecoveryCode(recover.Code);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Enabling TOTP is already committed. Recovery-code retrieval is a
            // separate, retryable read and must not make activation look failed.
            return TotpActivationResult.EnabledWithoutRecoveryCode();
        }
    }

    public async Task<string> GetRecoveryCodeAsync(string pw, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        var persisted = LoadPersisted(lease);
        var mphash = ComputeHash(pw, persisted);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);
        var recover = await api.GetRecoverAsync(new PasswordVerifyRequest(mphash), linked.Token);
        _session.ThrowIfExpired(lease);
        return recover.Code ?? string.Empty;
    }

    // -----------------------------------------------------------------------
    // BeginEmailSetupAsync — 拿到服务端返回的目标邮箱（脱敏版本）
    // -----------------------------------------------------------------------

    public async Task<string?> BeginEmailSetupAsync(string pw, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        var persisted = LoadPersisted(lease);
        var mphash = ComputeHash(pw, persisted);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);
        var response = await api.GetEmailAsync(new PasswordVerifyRequest(mphash), linked.Token);
        _session.ThrowIfExpired(lease);
        return response.Email;
    }

    // -----------------------------------------------------------------------
    // SendEmailAsync — 触发服务端向指定邮箱发送验证码
    // -----------------------------------------------------------------------

    public async Task SendEmailAsync(string pw, string email, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        var persisted = LoadPersisted(lease);
        var mphash = ComputeHash(pw, persisted);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.SendEmailAsync(new SendEmailRequest(Email: email, MasterPasswordHash: mphash), linked.Token);
        _session.ThrowIfExpired(lease);
    }

    // -----------------------------------------------------------------------
    // EnableEmailAsync — 提交邮箱 token 完成 Email 两步验证启用
    // -----------------------------------------------------------------------

    public async Task EnableEmailAsync(string pw, string email, string token, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        var persisted = LoadPersisted(lease);
        var mphash = ComputeHash(pw, persisted);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.EnableEmailAsync(
            new EmailVerifyRequest(Email: email, Token: token, MasterPasswordHash: mphash), linked.Token);
        _session.ThrowIfExpired(lease);
    }

    public Task<WebAuthnTwoFactorDetails> GetWebAuthnAsync(
        string pw,
        CancellationToken ct = default) =>
        GetWebAuthnAsync(TwoFactorUserVerification.FromMasterPassword(pw), ct);

    public async Task<WebAuthnTwoFactorDetails> GetWebAuthnAsync(
        TwoFactorUserVerification verification,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verification);
        var lease = _session.CaptureLease();
        var request = CreateVerificationRequest(verification, lease);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);
        var response = await api.GetWebAuthnAsync(request, linked.Token);
        _session.ThrowIfExpired(lease);
        return MapWebAuthnDetails(response);
    }

    public async Task RequestProtectedActionOtpAsync(CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        await api.RequestProtectedActionOtpAsync(linked.Token);
        _session.ThrowIfExpired(lease);
    }

    public Task<WebAuthnRegistrationChallenge> GetWebAuthnChallengeAsync(
        string pw,
        CancellationToken ct = default) =>
        GetWebAuthnChallengeAsync(TwoFactorUserVerification.FromMasterPassword(pw), ct);

    public async Task<WebAuthnRegistrationChallenge> GetWebAuthnChallengeAsync(
        TwoFactorUserVerification verification,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verification);
        var lease = _session.CaptureLease();
        var initialVerification = CreateVerificationRequest(verification, lease);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);

        var verificationResponse = await api.GetWebAuthnAsync(
            initialVerification,
            linked.Token);
        _session.ThrowIfExpired(lease);
        var current = MapWebAuthnDetails(verificationResponse);

        var response = await api.GetWebAuthnChallengeAsync(
            CreateContinuationVerification(initialVerification, verificationResponse.UserVerificationToken),
            linked.Token);
        _session.ThrowIfExpired(lease);
        if (response.Options is not JsonElement options)
            throw new TwoFactorOperationException("The server did not return a WebAuthn registration challenge.");

        var challenge = WebAuthnTwoFactorRegistrationProtocol.ParseChallenge(
            options,
            lease.Endpoints,
            verificationResponse.UserVerificationToken);
        challenge.BindTo(
            lease.AccountId,
            lease.Generation,
            lease.Endpoints,
            current.Credentials.Select(static credential => credential.Id));
        return challenge;
    }

    public Task<WebAuthnRegistrationOutcome> CompleteWebAuthnRegistrationAsync(
        string pw,
        int id,
        string name,
        WebAuthnRegistrationChallenge challenge,
        WebAuthnRegistrationDeviceResponse deviceResponse,
        CancellationToken ct = default) =>
        CompleteWebAuthnRegistrationAsync(
            TwoFactorUserVerification.FromMasterPassword(pw),
            id,
            name,
            challenge,
            deviceResponse,
            ct);

    public async Task<WebAuthnRegistrationOutcome> CompleteWebAuthnRegistrationAsync(
        TwoFactorUserVerification verification,
        int id,
        string name,
        WebAuthnRegistrationChallenge challenge,
        WebAuthnRegistrationDeviceResponse deviceResponse,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(deviceResponse);
        if (id <= 0)
            throw new TwoFactorOperationException("The WebAuthn credential identifier is invalid.");
        var normalizedName = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName)
            || normalizedName.Length > MaxWebAuthnCredentialNameLength)
        {
            throw new TwoFactorOperationException("The WebAuthn credential name is invalid.");
        }

        var lease = _session.CaptureLease();
        if (!challenge.IsBoundTo(lease.AccountId, lease.Generation, lease.Endpoints))
            throw new TwoFactorOperationException("The WebAuthn registration challenge is no longer current.");
        if (challenge.CredentialIdExistedAtIssue(id))
            throw new TwoFactorOperationException("The WebAuthn credential identifier is already in use.");

        var initialVerification = CreateVerificationRequest(verification, lease);
        var continuationVerification = CreateContinuationVerification(
            initialVerification,
            challenge.UserVerificationToken);
        WebAuthnTwoFactorRegistrationProtocol.ValidateDeviceResponse(challenge, deviceResponse);
        ct.ThrowIfCancellationRequested();
        _session.ThrowIfExpired(lease);
        if (!challenge.TryConsume())
            throw new TwoFactorOperationException("The WebAuthn registration challenge has already been used.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        var credentialId = WebAuthnCrypto.EncodeBase64Url(deviceResponse.CredentialId);
        var request = EnableWebAuthnRequest.Create(
            id,
            normalizedName,
            new WebAuthnRegistrationCredential(
                credentialId,
                credentialId,
                new WebAuthnAttestationResponse(
                    WebAuthnCrypto.EncodeBase64Url(deviceResponse.AttestationObject),
                    WebAuthnCrypto.EncodeBase64Url(deviceResponse.ClientDataJson),
                    deviceResponse.Transports.ToArray())),
            continuationVerification);

        try
        {
            var response = await api.EnableWebAuthnAsync(request, linked.Token);
            _session.ThrowIfExpired(lease);
            var details = MapWebAuthnDetails(response);
            if (!ContainsRegistration(details, id, normalizedName))
                throw new InvalidDataException("The WebAuthn registration response did not contain the new credential.");
            return new WebAuthnRegistrationOutcome.Confirmed(details, Reconciled: false);
        }
        catch (Exception exception) when (IsPotentiallyCommittedWrite(exception))
        {
            var observed = await TryReconcileWebAuthnAsync(
                api,
                lease,
                initialVerification);
            return observed is not null && ContainsRegistration(observed, id, normalizedName)
                ? new WebAuthnRegistrationOutcome.Confirmed(observed, Reconciled: true)
                : new WebAuthnRegistrationOutcome.OutcomeUncertain(observed);
        }
    }

    public Task<WebAuthnTwoFactorDetails> DeleteWebAuthnCredentialAsync(
        string pw,
        int id,
        CancellationToken ct = default) =>
        DeleteWebAuthnCredentialAsync(TwoFactorUserVerification.FromMasterPassword(pw), id, ct);

    public async Task<WebAuthnTwoFactorDetails> DeleteWebAuthnCredentialAsync(
        TwoFactorUserVerification verification,
        int id,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verification);
        if (id <= 0)
            throw new TwoFactorOperationException("The WebAuthn credential identifier is invalid.");

        var lease = _session.CaptureLease();
        var initialVerification = CreateVerificationRequest(verification, lease);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);

        var verificationResponse = await api.GetWebAuthnAsync(
            initialVerification,
            linked.Token);
        _session.ThrowIfExpired(lease);
        var before = MapWebAuthnDetails(verificationResponse);
        if (!before.Credentials.Any(credential => credential.Id == id))
            throw new TwoFactorOperationException("The WebAuthn credential was not found.");

        if (before.Credentials.Count == 1
            && !string.IsNullOrWhiteSpace(verificationResponse.UserVerificationToken))
        {
            return await DeleteAllOfficialWebAuthnCredentialsAsync(
                api,
                lease,
                initialVerification,
                verificationResponse.UserVerificationToken,
                linked.Token);
        }

        var continuationVerification = CreateContinuationVerification(
            initialVerification,
            verificationResponse.UserVerificationToken);
        var details = await DeleteOneWebAuthnCredentialAsync(
            api,
            lease,
            id,
            initialVerification,
            continuationVerification,
            linked.Token);
        if (details.Credentials.Count == 0 && details.Enabled)
        {
            if (!string.IsNullOrWhiteSpace(verificationResponse.UserVerificationToken))
            {
                return await DeleteAllOfficialWebAuthnCredentialsAsync(
                    api,
                    lease,
                    initialVerification,
                    verificationResponse.UserVerificationToken,
                    lease.CancellationToken);
            }
            return await DisableVaultwardenWebAuthnProviderAsync(
                api,
                lease,
                initialVerification,
                lease.CancellationToken);
        }
        return details;
    }

    // -----------------------------------------------------------------------
    // DisableAsync — 禁用指定类型的两步验证提供者
    // -----------------------------------------------------------------------

    public async Task DisableAsync(string pw, int type, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        var persisted = LoadPersisted(lease);
        var mphash = ComputeHash(pw, persisted);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.DisableAsync(new DisableTwoFactorRequest(MasterPasswordHash: mphash, Type: type), linked.Token);
        _session.ThrowIfExpired(lease);
    }

    // -----------------------------------------------------------------------
    // 私有辅助
    // -----------------------------------------------------------------------

    /// <summary>
    /// 加载持久化会话，失败则抛 <see cref="TwoFactorOperationException"/>。
    /// </summary>
    private PersistedSession LoadPersisted(Core.Session.VaultSessionLease lease)
    {
        if (!_tokenStore.TryLoad(out var persisted)
            || !_session.IsCurrent(lease, persisted))
            throw new TwoFactorOperationException("持久化会话与当前账户不匹配，请重新登录后重试。");
        return persisted;
    }

    /// <summary>
    /// 从持久化会话派生 masterKey，计算 masterPasswordHash，然后无条件清零 masterKey。
    /// </summary>
    private string ComputeHash(string pw, PersistedSession persisted)
    {
        var mk = _crypto.DeriveMasterKey(
            pw,
            persisted.EffectiveMasterPasswordSalt,
            persisted.KdfType,
            persisted.KdfIterations,
            persisted.KdfMemory,
            persisted.KdfParallelism);

        try
        {
            return _crypto.ComputeMasterPasswordHash(mk, pw);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mk);
        }
    }

    private WebAuthnVerificationRequest CreateVerificationRequest(
        TwoFactorUserVerification verification,
        Core.Session.VaultSessionLease lease) => verification.Kind switch
    {
        TwoFactorUserVerificationKind.MasterPassword =>
            WebAuthnVerificationRequest.FromMasterPasswordHash(
                ComputeHash(verification.Secret, LoadPersisted(lease))),
        TwoFactorUserVerificationKind.Otp =>
            WebAuthnVerificationRequest.FromOtp(verification.Secret),
        TwoFactorUserVerificationKind.AuthRequestAccessCode =>
            WebAuthnVerificationRequest.FromAuthRequestAccessCode(verification.Secret),
        _ => throw new ArgumentOutOfRangeException(nameof(verification)),
    };

    private static WebAuthnVerificationRequest CreateContinuationVerification(
        WebAuthnVerificationRequest initialVerification,
        string? userVerificationToken) =>
        string.IsNullOrWhiteSpace(userVerificationToken)
            ? initialVerification
            : WebAuthnVerificationRequest.FromUserVerificationToken(userVerificationToken);

    private async Task<WebAuthnTwoFactorDetails> DeleteOneWebAuthnCredentialAsync(
        ITwoFactorApiClient api,
        Core.Session.VaultSessionLease lease,
        int id,
        WebAuthnVerificationRequest initialVerification,
        WebAuthnVerificationRequest continuationVerification,
        CancellationToken ct)
    {
        WebAuthnTwoFactorDetails details;
        try
        {
            var response = await api.DeleteWebAuthnCredentialAsync(
                DeleteWebAuthnCredentialRequest.Create(id, continuationVerification),
                ct);
            _session.ThrowIfExpired(lease);
            details = MapWebAuthnDetails(response);
        }
        catch (Exception exception) when (IsPotentiallyCommittedWrite(exception))
        {
            details = await TryReconcileWebAuthnAsync(api, lease, initialVerification)
                ?? throw new TwoFactorOperationException(
                    "The WebAuthn credential deletion result could not be confirmed.");
        }

        if (details.Credentials.Any(credential => credential.Id == id))
            throw new TwoFactorOperationException(
                "The WebAuthn credential deletion result could not be confirmed.");
        return details;
    }

    private async Task<WebAuthnTwoFactorDetails> DeleteAllOfficialWebAuthnCredentialsAsync(
        ITwoFactorApiClient api,
        Core.Session.VaultSessionLease lease,
        WebAuthnVerificationRequest initialVerification,
        string userVerificationToken,
        CancellationToken ct)
    {
        try
        {
            await api.DeleteAllWebAuthnCredentialsAsync(
                DeleteAllWebAuthnCredentialsRequest.FromUserVerificationToken(userVerificationToken),
                ct);
            _session.ThrowIfExpired(lease);
            return EmptyWebAuthnDetails();
        }
        catch (Exception exception) when (IsPotentiallyCommittedWrite(exception))
        {
            var observed = await TryReconcileWebAuthnAsync(api, lease, initialVerification);
            if (observed is not null && !observed.Enabled && observed.Credentials.Count == 0)
                return observed;
            throw new TwoFactorOperationException(
                "The WebAuthn provider deletion result could not be confirmed.");
        }
    }

    private async Task<WebAuthnTwoFactorDetails> DisableVaultwardenWebAuthnProviderAsync(
        ITwoFactorApiClient api,
        Core.Session.VaultSessionLease lease,
        WebAuthnVerificationRequest initialVerification,
        CancellationToken ct)
    {
        try
        {
            await api.DisableAsync(CreateDisableWebAuthnRequest(initialVerification), ct);
            _session.ThrowIfExpired(lease);
            return EmptyWebAuthnDetails();
        }
        catch (Exception exception) when (IsPotentiallyCommittedWrite(exception))
        {
            var observed = await TryReconcileWebAuthnAsync(api, lease, initialVerification);
            if (observed is not null && !observed.Enabled && observed.Credentials.Count == 0)
                return observed;
            throw new TwoFactorOperationException(
                "The WebAuthn provider disable result could not be confirmed.");
        }
    }

    private static DisableTwoFactorRequest CreateDisableWebAuthnRequest(
        WebAuthnVerificationRequest verification) =>
        new(
            verification.MasterPasswordHash,
            (int)TwoFactorProviderType.WebAuthn,
            verification.Otp,
            verification.AuthRequestAccessCode);

    private async Task<WebAuthnTwoFactorDetails?> TryReconcileWebAuthnAsync(
        ITwoFactorApiClient api,
        Core.Session.VaultSessionLease lease,
        WebAuthnVerificationRequest initialVerification)
    {
        if (!_session.IsCurrent(lease))
            return null;

        try
        {
            var response = await api.GetWebAuthnAsync(
                initialVerification,
                lease.CancellationToken);
            _session.ThrowIfExpired(lease);
            return MapWebAuthnDetails(response);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static WebAuthnTwoFactorDetails EmptyWebAuthnDetails() =>
        new(false, Array.Empty<WebAuthnTwoFactorCredential>());

    private static WebAuthnTwoFactorDetails MapWebAuthnDetails(WebAuthnTwoFactorResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Keys is null || response.Keys.Count > 256)
            throw new InvalidDataException("The WebAuthn credential list is invalid.");

        var credentials = new List<WebAuthnTwoFactorCredential>(response.Keys.Count);
        var ids = new HashSet<int>();
        foreach (var item in response.Keys)
        {
            var name = item.Name?.Trim() ?? string.Empty;
            if (item.Id <= 0
                || !ids.Add(item.Id)
                || name.Length > MaxWebAuthnCredentialNameLength)
            {
                throw new InvalidDataException("The WebAuthn credential list is invalid.");
            }
            credentials.Add(new WebAuthnTwoFactorCredential(item.Id, name, item.Migrated));
        }
        return new WebAuthnTwoFactorDetails(response.Enabled, credentials);
    }

    private static bool ContainsRegistration(
        WebAuthnTwoFactorDetails details,
        int id,
        string name) =>
        details.Credentials.Any(credential =>
            credential.Id == id
            && string.Equals(credential.Name, name, StringComparison.Ordinal));

    private static bool IsPotentiallyCommittedWrite(Exception exception)
    {
        if (exception is VaultWriteException writeException)
        {
            return writeException.StatusCode == HttpStatusCode.RequestTimeout
                || writeException.StatusCode is { } statusCode && (int)statusCode >= 500;
        }

        return exception is HttpRequestException
            or IOException
            or OperationCanceledException
            or TimeoutException
            or JsonException
            or InvalidDataException;
    }
}
