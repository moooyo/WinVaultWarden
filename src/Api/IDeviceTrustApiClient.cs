using Api.Dtos;

namespace Api;

public interface IDeviceTrustApiClient : IAuthenticatedApiRole
{
    Task<DeviceDto> UpdateTrustedDeviceKeysAsync(
        string deviceIdentifier,
        TrustedDeviceKeysRequest request,
        CancellationToken ct = default);

    Task UntrustDevicesAsync(
        UntrustDevicesRequest request,
        CancellationToken ct = default);
}
