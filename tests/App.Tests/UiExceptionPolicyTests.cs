using App.Services;
using Api;
using Xunit;

namespace App.Tests;

public sealed class UiExceptionPolicyTests
{
    [Fact]
    public void ExplicitRecoverableExceptions_CanContinue()
    {
        Assert.True(UiExceptionPolicy.CanContinue(
            new OperationCanceledException(new CancellationToken(canceled: true))));
        Assert.True(UiExceptionPolicy.CanContinue(
            new RecoverableUiException("recoverable")));
    }

    [Fact]
    public void BareCancellationWithoutCancelledToken_CannotBeSilentlyHandled()
    {
        Assert.False(UiExceptionPolicy.CanContinue(new OperationCanceledException()));
        Assert.False(UiExceptionPolicy.IsGenuineCancellation(
            new OperationCanceledException(),
            CancellationToken.None));
    }

    [Fact]
    public void UnknownException_CannotContinue()
    {
        Assert.False(UiExceptionPolicy.CanContinue(
            new InvalidOperationException("unexpected")));
    }

    [Fact]
    public void TimeoutShapedCancellation_CannotBeSilentlyHandledAsUserCancellation()
    {
        var timeout = new TaskCanceledException(
            "request timed out",
            new RequestTimeoutException(
                RequestTimeoutPhase.UploadResponseHeaders,
                TimeSpan.FromSeconds(30)),
            CancellationToken.None);

        Assert.False(UiExceptionPolicy.CanContinue(timeout));
    }

    [Fact]
    public void WrappingUnknownInAggregate_DoesNotMakeItRecoverable()
    {
        Assert.False(UiExceptionPolicy.CanContinue(
            new AggregateException(new InvalidOperationException("unexpected"))));
    }
}
