using Core.Models;
using Core.Session;

namespace Core.Services;

public interface ILoginWithDeviceService
{
    Task<LoginWithDeviceBeginResult> BeginAsync(
        AuthenticationAttemptLease attempt,
        ServerEndpoints endpoints,
        string email,
        string deviceIdentifier,
        CancellationToken ct = default);

    Task<LoginWithDevicePollResult> PollAsync(
        AuthenticationAttemptLease attempt,
        CancellationToken ct = default);

    void Cancel(AuthenticationAttemptLease attempt);
}

public enum LoginWithDeviceFailureReason
{
    UnknownDevice,
    NoPendingRequest,
    InvalidServerResponse,
}

public sealed class LoginWithDeviceException : Exception
{
    public LoginWithDeviceException(
        LoginWithDeviceFailureReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public LoginWithDeviceFailureReason Reason { get; }
}
