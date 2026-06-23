using App.ViewModels.Models;

namespace App.Services;

public interface IDeviceUiService
{
    IReadOnlyList<DeviceItem> GetDevices();
}

public sealed class MockDeviceUiService : IDeviceUiService
{
    public IReadOnlyList<DeviceItem> GetDevices() => new List<DeviceItem>
    {
        new("d1", "Windows 桌面", "", "刚刚 · 本机", IsCurrent: true),
        new("d2", "iPhone 15", "", "2 小时前 · 北京", IsCurrent: false),
        new("d3", "Chrome 浏览器", "", "昨天 · 上海", IsCurrent: false),
    };
}
