using App.Services;
using Xunit;

namespace App.Tests;

public sealed class TitleBarLayoutPolicyTests
{
    [Fact]
    public void MeasuredJapaneseTabsDoNotAllowCenteredSearchAt1280()
    {
        Assert.False(TitleBarLayoutPolicy.CanCenterSearch(1280, 491.33, 970));
        Assert.True(TitleBarLayoutPolicy.CanCenterSearch(1440, 491.33, 1130));
    }

    [Theory]
    [InlineData(1280, 350, 1050, true)]
    [InlineData(1280, 350, 810, false)]
    [InlineData(1280, 0, 1050, false)]
    [InlineData(920, 350, 700, false)]
    public void CenteredSearchRequiresMeasuredSpaceOnBothSides(double width, double tabs, double actions, bool expected) =>
        Assert.Equal(expected, TitleBarLayoutPolicy.CanCenterSearch(width, tabs, actions));

    [Theory]
    [InlineData(720, 336, 22, 212, 138, 32, 0, true)]
    [InlineData(920, 336, 22, 170, 138, 32, 180, false)]
    [InlineData(800, 336, 22, 170, 138, 32, 180, true)]
    [InlineData(640, 336, 22, 212, 138, 32, 0, true)]
    public void LocalizedTabsUseIconsOnlyWhenLabelsConsumeTheRequiredSlot(
        double width, double tabs, double brand, double actions, double caption, double insets, double search, bool expected) =>
        Assert.Equal(expected, TitleBarLayoutPolicy.NeedCompactTabs(width, tabs, brand, actions, caption, insets, search));
}
