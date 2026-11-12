using Core.WindowsPasskeys;
using Xunit;

namespace Crypto.Tests;

public sealed class WindowsPasskeyFailurePolicyTests
{
    [Fact]
    public void ActualRequestCancellationMapsToUserCancelled()
    {
        using var request = new CancellationTokenSource();
        request.Cancel();
        var exception = new OperationCanceledException(request.Token);

        Assert.Equal(WindowsPasskeyException.UserCancelled,
            WindowsPasskeyFailurePolicy.ToHResult(exception, request.Token));
    }

    [Fact]
    public void HttpClientStyleTimeoutMapsToTimeoutHResult()
    {
        var exception = new TaskCanceledException("Request deadline", new TimeoutException("Network timeout"));

        Assert.Equal(WindowsPasskeyFailurePolicy.RequestTimeout,
            WindowsPasskeyFailurePolicy.ToHResult(exception));
    }

    [Fact]
    public void BareUnownedTaskCancellationIsAProtocolFailure()
    {
        Assert.Equal(WindowsPasskeyFailurePolicy.UnexpectedFailure,
            WindowsPasskeyFailurePolicy.ToHResult(new TaskCanceledException()));
    }

    [Fact]
    public void AnotherCancelledTokenCannotCancelThisRequest()
    {
        using var otherRequest = new CancellationTokenSource();
        otherRequest.Cancel();

        Assert.Equal(WindowsPasskeyFailurePolicy.UnexpectedFailure,
            WindowsPasskeyFailurePolicy.ToHResult(new OperationCanceledException(otherRequest.Token)));
    }

    [Fact]
    public void ExplicitLeaseCancellationPreservesItsProtocolResult()
    {
        Assert.Equal(WindowsPasskeyException.UserCancelled,
            WindowsPasskeyFailurePolicy.ToHResult(new WindowsPasskeyException(
                WindowsPasskeyException.UserCancelled, "The original session expired.")));
    }

    [Fact]
    public void UnexpectedFailureNeverLeaksItsMessageThroughTheProtocol()
    {
        Assert.Equal(WindowsPasskeyFailurePolicy.UnexpectedFailure,
            WindowsPasskeyFailurePolicy.ToHResult(new InvalidOperationException("Private application state")));
    }

    [Fact]
    public void RequestCancellationWinsAnOtherwiseConcurrentNetworkTimeout()
    {
        using var request = new CancellationTokenSource();
        request.Cancel();
        var exception = new TaskCanceledException("Deadline", new TimeoutException());

        Assert.Equal(WindowsPasskeyException.UserCancelled,
            WindowsPasskeyFailurePolicy.ToHResult(exception, request.Token));
    }
}
