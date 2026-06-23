namespace App.ViewModels.Models;

// 设备列表项。Glyph 为 Segoe Fluent 图标(桌面/手机/浏览器)。
public sealed record DeviceItem(
    string Id,
    string Name,
    string Glyph,
    string LastActive,
    bool IsCurrent);
