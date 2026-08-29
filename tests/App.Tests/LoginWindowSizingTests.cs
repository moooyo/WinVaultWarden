using App.Services;
using Xunit;

namespace App.Tests;

public sealed class LoginWindowSizingTests
{
    private static readonly WindowSizeDip PreferredMinimum = new(320, 500);
    private static readonly WindowSizeDip ConfiguredMaximum = new(520, 820);

    [Fact]
    public void ClampToWorkArea_PreservesDesiredSizeOnNormalDisplay()
    {
        var result = Clamp(new WindowSizeDip(440, 540), new WindowSizeDip(1920, 1080));

        Assert.Equal(new WindowSizeDip(440, 540), result);
    }

    [Fact]
    public void ClampToWorkArea_UsesConfiguredMinimumAndMaximumWhenSpaceAllows()
    {
        Assert.Equal(
            PreferredMinimum,
            Clamp(new WindowSizeDip(200, 300), new WindowSizeDip(1920, 1080)));
        Assert.Equal(
            ConfiguredMaximum,
            Clamp(new WindowSizeDip(900, 1000), new WindowSizeDip(1920, 1080)));
    }

    [Fact]
    public void ClampToWorkArea_ConstrainsBothAxesToWorkAreaMinusMargins()
    {
        var result = Clamp(new WindowSizeDip(440, 540), new WindowSizeDip(400, 560));

        Assert.Equal(new WindowSizeDip(320, 480), result);
    }

    [Fact]
    public void ClampToWorkArea_AllowsSizeBelowPreferredMinimumOnSmallDisplay()
    {
        var result = Clamp(new WindowSizeDip(440, 540), new WindowSizeDip(300, 420));

        Assert.Equal(new WindowSizeDip(220, 340), result);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ClampToWorkArea_RejectsNonFiniteInput(double invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Clamp(
            new WindowSizeDip(invalid, 540),
            new WindowSizeDip(1920, 1080)));
    }

    private static WindowSizeDip Clamp(WindowSizeDip desired, WindowSizeDip workArea) =>
        LoginWindowSizing.ClampToWorkArea(
            desired,
            workArea,
            PreferredMinimum,
            ConfiguredMaximum,
            workAreaMarginPerEdge: 40);
}
