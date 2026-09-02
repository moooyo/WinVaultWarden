using Api;
using Api.Dtos;

namespace Vault.Tests;

/// <summary>
/// ITwoFactorApiClient 的测试桩：记录所有发出的请求，返回预设的罐头响应。
/// 多次调用时，罐头列表按顺序消费（Dequeue）；耗尽后返回最后一个。
/// </summary>
public sealed class FakeTwoFactorApiClient : ITwoFactorApiClient
{
    // ---------- 记录请求 ----------

    public PasswordVerifyRequest? LastPasswordVerify;
    public EnableAuthenticatorRequest? LastEnableAuthenticator;
    public DisableAuthenticatorRequest? LastDisableAuthenticator;
    public SendEmailRequest? LastSendEmail;
    public EmailVerifyRequest? LastEnableEmail;
    public WebAuthnVerificationRequest? LastGetWebAuthn;
    public WebAuthnVerificationRequest? LastGetWebAuthnChallenge;
    public EnableWebAuthnRequest? LastEnableWebAuthn;
    public DeleteWebAuthnCredentialRequest? LastDeleteWebAuthn;
    public DeleteAllWebAuthnCredentialsRequest? LastDeleteAllWebAuthn;
    public DisableTwoFactorRequest? LastDisable;

    // ---------- 罐头响应 ----------

    public TwoFactorProvidersResponse ProvidersResponse { get; set; } = new(new List<TwoFactorProviderItem>());

    public AuthenticatorResponse AuthenticatorResponse { get; set; } = new(Enabled: false, Key: "FAKEBASE32SECRET");

    public EmailStatusResponse EmailStatusResponse { get; set; } = new(Email: "u@example.com", Enabled: false);

    public RecoverResponse RecoverResponse { get; set; } = new(Code: "recover-code-abc");

    public WebAuthnTwoFactorResponse WebAuthnResponse { get; set; } =
        new(false, Array.Empty<WebAuthnCredentialItem>());

    public WebAuthnTwoFactorResponse? EnableWebAuthnResponse { get; set; }

    public WebAuthnTwoFactorResponse? DeleteWebAuthnResponse { get; set; }

    public Queue<WebAuthnTwoFactorResponse> WebAuthnResponses { get; } = new();

    public WebAuthnChallengeResponse WebAuthnChallengeResponse { get; set; } = new(null);

    public Exception? EnableAuthenticatorException { get; set; }

    public Exception? GetRecoverException { get; set; }

    public Exception? EnableWebAuthnException { get; set; }

    public Exception? DeleteWebAuthnException { get; set; }

    public Exception? DeleteAllWebAuthnException { get; set; }

    public Exception? RequestProtectedActionOtpException { get; set; }

    public Exception? DisableException { get; set; }

    public int EnableAuthenticatorCalls { get; private set; }

    public int GetRecoverCalls { get; private set; }

    public int GetWebAuthnCalls { get; private set; }

    public int EnableWebAuthnCalls { get; private set; }

    public int DeleteWebAuthnCalls { get; private set; }

    public int DeleteAllWebAuthnCalls { get; private set; }

    public int RequestProtectedActionOtpCalls { get; private set; }

    public int DisableCalls { get; private set; }

    // 若设置则所有调用都抛出此异常（模拟网络/服务端错误）
    public Exception? Throw;

    // ---------- 实现 ----------

    public Task<TwoFactorProvidersResponse> GetProvidersAsync(CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        return Task.FromResult(ProvidersResponse);
    }

    public Task<AuthenticatorResponse> GetAuthenticatorAsync(PasswordVerifyRequest request, CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        LastPasswordVerify = request;
        return Task.FromResult(AuthenticatorResponse);
    }

    public Task<AuthenticatorResponse> EnableAuthenticatorAsync(EnableAuthenticatorRequest request, CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        EnableAuthenticatorCalls++;
        if (EnableAuthenticatorException is not null) throw EnableAuthenticatorException;
        LastEnableAuthenticator = request;
        // 模拟服务端启用成功后返回 enabled=true
        return Task.FromResult(new AuthenticatorResponse(Enabled: true, Key: request.Key));
    }

    public Task DisableAuthenticatorAsync(DisableAuthenticatorRequest request, CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        LastDisableAuthenticator = request;
        return Task.CompletedTask;
    }

    public Task<EmailStatusResponse> GetEmailAsync(PasswordVerifyRequest request, CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        LastPasswordVerify = request;
        return Task.FromResult(EmailStatusResponse);
    }

    public Task SendEmailAsync(SendEmailRequest request, CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        LastSendEmail = request;
        return Task.CompletedTask;
    }

    public Task EnableEmailAsync(EmailVerifyRequest request, CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        LastEnableEmail = request;
        return Task.CompletedTask;
    }

    public Task<RecoverResponse> GetRecoverAsync(PasswordVerifyRequest request, CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        GetRecoverCalls++;
        if (GetRecoverException is not null) throw GetRecoverException;
        LastPasswordVerify = request;
        return Task.FromResult(RecoverResponse);
    }

    public Task<WebAuthnTwoFactorResponse> GetWebAuthnAsync(
        WebAuthnVerificationRequest request,
        CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        GetWebAuthnCalls++;
        LastGetWebAuthn = request;
        return Task.FromResult(
            WebAuthnResponses.Count > 0
                ? WebAuthnResponses.Dequeue()
                : WebAuthnResponse);
    }

    public Task<WebAuthnChallengeResponse> GetWebAuthnChallengeAsync(
        WebAuthnVerificationRequest request,
        CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        LastGetWebAuthnChallenge = request;
        return Task.FromResult(WebAuthnChallengeResponse);
    }

    public Task<WebAuthnTwoFactorResponse> EnableWebAuthnAsync(
        EnableWebAuthnRequest request,
        CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        EnableWebAuthnCalls++;
        LastEnableWebAuthn = request;
        if (EnableWebAuthnException is not null) throw EnableWebAuthnException;
        return Task.FromResult(EnableWebAuthnResponse ?? WebAuthnResponse);
    }

    public Task<WebAuthnTwoFactorResponse> DeleteWebAuthnCredentialAsync(
        DeleteWebAuthnCredentialRequest request,
        CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        DeleteWebAuthnCalls++;
        LastDeleteWebAuthn = request;
        if (DeleteWebAuthnException is not null) throw DeleteWebAuthnException;
        return Task.FromResult(DeleteWebAuthnResponse ?? WebAuthnResponse);
    }

    public Task DeleteAllWebAuthnCredentialsAsync(
        DeleteAllWebAuthnCredentialsRequest request,
        CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        DeleteAllWebAuthnCalls++;
        LastDeleteAllWebAuthn = request;
        if (DeleteAllWebAuthnException is not null) throw DeleteAllWebAuthnException;
        return Task.CompletedTask;
    }

    public Task RequestProtectedActionOtpAsync(CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        RequestProtectedActionOtpCalls++;
        if (RequestProtectedActionOtpException is not null) throw RequestProtectedActionOtpException;
        return Task.CompletedTask;
    }

    public Task DisableAsync(DisableTwoFactorRequest request, CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        DisableCalls++;
        LastDisable = request;
        if (DisableException is not null) throw DisableException;
        return Task.CompletedTask;
    }
}
