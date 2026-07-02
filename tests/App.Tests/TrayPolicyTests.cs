using App.Services;
using Xunit;

namespace App.Tests;

public class TrayPolicyTests
{
    [Theory]
    [InlineData(true, true, true)]     // 显示图标 + 最小化到托盘 → 隐藏
    [InlineData(true, false, false)]   // 显示图标但未开最小化到托盘 → 不隐藏
    [InlineData(false, true, false)]   // 未显示图标 → 即便开了也不隐藏（防无入口）
    [InlineData(false, false, false)]
    public void ShouldHideOnMinimize_Gates_On_ShowIcon(bool showIcon, bool minToTray, bool expected)
    {
        Assert.Equal(expected, TrayPolicy.ShouldHideOnMinimize(showIcon, minToTray));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]   // 未显示图标 → 关闭走正常退出，不隐藏
    [InlineData(false, false, false)]
    public void ShouldHideOnClose_Gates_On_ShowIcon(bool showIcon, bool closeToTray, bool expected)
    {
        Assert.Equal(expected, TrayPolicy.ShouldHideOnClose(showIcon, closeToTray));
    }
}
