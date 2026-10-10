using Api.Dtos;

namespace Api;

public interface IIdentityApiClient : IAnonymousApiRole
{
    Task<ConfigResponse> GetConfigAsync(CancellationToken ct = default);
    Task<PreloginResponse> PreloginAsync(string email, CancellationToken ct = default);
    Task RequestPasswordHintAsync(string email, CancellationToken ct = default) =>
        Task.FromException(
            new NotSupportedException("Password hints are unavailable."));
    Task<ConnectTokenResult> ConnectTokenAsync(
        ConnectTokenRequest request,
        CancellationToken ct = default);
    Task SendTwoFactorEmailLoginAsync(
        SendEmailLoginRequest request,
        CancellationToken ct = default);
    Task ResendNewDeviceOtpAsync(
        ResendNewDeviceOtpRequest request,
        CancellationToken ct = default) =>
        Task.FromException(
            new NotSupportedException("New-device OTP resend is unavailable."));
}
