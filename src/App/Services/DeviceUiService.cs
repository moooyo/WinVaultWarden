using App.ViewModels.Models;
using Api;
using Core.Models;
using Core.Services;
using Vault;

namespace App.Services;

public sealed class DeviceUiService : IDeviceUiService
{
    private const string GlyphDesktop = "\uE770";
    private const string GlyphPhone = "\uE8EA";
    private const string GlyphBrowser = "\uE774";

    private readonly IVaultService _vault;
    private readonly string? _currentIdentifier;
    private readonly IVaultApiClientProvider? _clients;
    private readonly VaultSession? _session;

    public DeviceUiService(IVaultService vault)
        : this(vault, null)
    {
    }

    public DeviceUiService(IVaultService vault, string? currentIdentifier,
        IVaultApiClientProvider? clients = null, VaultSession? session = null)
    {
        _vault = vault;
        _currentIdentifier = currentIdentifier;
        _clients = clients;
        _session = session;
    }

    public IReadOnlyList<DeviceItem> GetDevices() =>
        _vault.GetDevices().Select(Map).ToList();

    /// <summary>Refreshes registration metadata from the existing authenticated devices endpoint.</summary>
    public async Task<IReadOnlyList<DeviceItem>> RefreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_clients is null || _session is null || _session.IsDemo)
            return GetDevices();

        var lease = _session.CaptureLease();
        if (!_session.IsUnlocked(lease))
            throw new InvalidOperationException("The vault must be unlocked to refresh devices.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IReadonlyApiClient>(lease);
        var response = await api.GetDevicesAsync(linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        var devices = response.Data.Select(device => new DeviceInfo(
            device.Id, device.Name, device.Type, device.Identifier,
            device.CreationDate, device.IsTrusted)).ToArray();
        var published = false;
        _session.TryRunIfUnlocked(lease, () =>
        {
            if (!linked.IsCancellationRequested)
            {
                _session.SetDevices(devices);
                published = true;
            }
        });
        if (!published)
            throw new OperationCanceledException(lease.CancellationToken.IsCancellationRequested
                ? lease.CancellationToken : linked.Token);
        return devices.Select(Map).ToArray();
    }

    private DeviceItem Map(DeviceInfo device)
    {
        var isCurrent = !string.IsNullOrWhiteSpace(_currentIdentifier)
            && (string.Equals(device.Identifier, _currentIdentifier, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.Id, _currentIdentifier, StringComparison.OrdinalIgnoreCase));

        return new DeviceItem(
            device.Id,
            string.IsNullOrWhiteSpace(device.Name) ? "未知设备" : device.Name,
            GlyphFor(device.Type),
            RegisteredText(device),
            isCurrent,
            device.IsTrusted);
    }

    private static string GlyphFor(int type) => type switch
    {
        6 => GlyphDesktop,
        8 or 9 => GlyphPhone,
        10 or 11 => GlyphBrowser,
        _ => GlyphDesktop,
    };

    private static string RegisteredText(DeviceInfo device)
    {
        var date = device.CreationDate?.LocalDateTime.ToString("yyyy/M/d HH:mm");
        return date is null ? "登记时间未知" : $"登记于 {date}";
    }
}
