using App.Services;
using Xunit;

namespace App.Tests;

public class ClipboardClearPolicyTests
{
    [Theory]
    [InlineData(0, 0)] [InlineData(1, 10)] [InlineData(2, 20)] [InlineData(3, 30)]
    [InlineData(4, 60)] [InlineData(5, 120)] [InlineData(6, 300)]
    [InlineData(7, 30)] [InlineData(-1, 30)] [InlineData(99, 30)]
    public void SecondsForIndex_Cases(int index, int expected) =>
        Assert.Equal(expected, ClipboardClearPolicy.SecondsForIndex(index));
}
