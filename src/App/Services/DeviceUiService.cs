using App.ViewModels.Models;
using Core.Models;
using Core.Services;

namespace App.Services;

public sealed class DeviceUiService : IDeviceUiService
{
    private const string GlyphDesktop = "\uE770";
    private const string GlyphPhone = "\uE8EA";
    private const string GlyphBrowser = "\uE774";

    private readonly IVaultService _vault;
    private readonly string? _currentIdentifier;

    public DeviceUiService(IVaultService vault)
        : this(vault, null)
    {
    }

    public DeviceUiService(IVaultService vault, string? currentIdentifier)
    {
        _vault = vault;
        _currentIdentifier = currentIdentifier;
    }

    public IReadOnlyList<DeviceItem> GetDevices() =>
        _vault.GetDevices().Select(Map).ToList();

    private DeviceItem Map(DeviceInfo device)
    {
        var isCurrent = !string.IsNullOrWhiteSpace(_currentIdentifier)
            && (string.Equals(device.Identifier, _currentIdentifier, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.Id, _currentIdentifier, StringComparison.OrdinalIgnoreCase));

        return new DeviceItem(
            device.Id,
            string.IsNullOrWhiteSpace(device.Name) ? "Unknown device" : device.Name,
            GlyphFor(device.Type),
            LastActiveText(device, isCurrent),
            isCurrent);
    }

    private static string GlyphFor(int type) => type switch
    {
        6 => GlyphDesktop,
        8 or 9 => GlyphPhone,
        10 or 11 => GlyphBrowser,
        _ => GlyphDesktop,
    };

    private static string LastActiveText(DeviceInfo device, bool isCurrent)
    {
        var date = device.CreationDate?.LocalDateTime.ToString("yyyy/M/d HH:mm") ?? string.Empty;
        return isCurrent && date.Length > 0 ? $"{date} · 本机" : date;
    }
}
