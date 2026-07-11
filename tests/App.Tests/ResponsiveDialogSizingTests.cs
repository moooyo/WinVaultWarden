using App.Services;
using Xunit;

namespace App.Tests;

public sealed class ResponsiveDialogSizingTests
{
    [Theory]
    [InlineData(1000, 620, 48, 620)]
    [InlineData(500, 620, 48, 452)]
    [InlineData(40, 620, 48, 0)]
    public void Clamp_UsesPreferredOrAvailableWidth(
        double root,
        double preferred,
        double margin,
        double expected)
    {
        Assert.Equal(expected, ResponsiveDialogSizing.Clamp(
            root, preferred, margin));
    }
}
