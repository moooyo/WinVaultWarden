using App.Services;
using Xunit;

namespace App.Tests;

public class IdleTimeoutPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 999, false)]                    // minutes<=0 → never
    [InlineData(-5, 999, false)]
    [InlineData(15, 14, false)]                    // 14 min < 15 → not expired
    [InlineData(15, 15, true)]                     // exactly 15 → expired
    [InlineData(15, 30, true)]
    public void IsExpired_Cases(int minutes, int elapsedMin, bool expected) =>
        Assert.Equal(expected, IdleTimeoutPolicy.IsExpired(T0, T0.AddMinutes(elapsedMin), minutes));

    [Theory]
    [InlineData(0, 0)] [InlineData(1, 1)] [InlineData(2, 5)] [InlineData(3, 15)]
    [InlineData(4, 30)] [InlineData(5, 60)] [InlineData(6, 240)] [InlineData(7, 0)]
    [InlineData(99, 0)] [InlineData(-1, 0)]
    public void MinutesForIndex_Cases(int index, int expected) =>
        Assert.Equal(expected, IdleTimeoutPolicy.MinutesForIndex(index));

    [Theory]
    [InlineData(0, VaultTimeoutAction.Lock)]
    [InlineData(1, VaultTimeoutAction.Logout)]
    [InlineData(2, VaultTimeoutAction.Lock)]
    public void ActionForIndex_Cases(int index, VaultTimeoutAction expected) =>
        Assert.Equal(expected, IdleTimeoutPolicy.ActionForIndex(index));
}
