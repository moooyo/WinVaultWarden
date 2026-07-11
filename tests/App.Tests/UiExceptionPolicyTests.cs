using App.Services;
using Xunit;

namespace App.Tests;

public sealed class UiExceptionPolicyTests
{
    [Fact]
    public void ExplicitRecoverableExceptions_CanContinue()
    {
        Assert.True(UiExceptionPolicy.CanContinue(
            new OperationCanceledException()));
        Assert.True(UiExceptionPolicy.CanContinue(
            new RecoverableUiException("recoverable")));
    }

    [Fact]
    public void UnknownException_CannotContinue()
    {
        Assert.False(UiExceptionPolicy.CanContinue(
            new InvalidOperationException("unexpected")));
    }

    [Fact]
    public void WrappingUnknownInAggregate_DoesNotMakeItRecoverable()
    {
        Assert.False(UiExceptionPolicy.CanContinue(
            new AggregateException(new InvalidOperationException("unexpected"))));
    }
}
