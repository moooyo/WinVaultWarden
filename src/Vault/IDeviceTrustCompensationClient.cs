using Api;
using Api.Dtos;
using Core.Session;

namespace Vault;

public interface IDeviceTrustCompensationClient
{
    Task UntrustAsync(
        AccessTokenBinding binding,
        string deviceId,
        CancellationToken ct = default);
}

public sealed class DeviceTrustCompensationClient : IDeviceTrustCompensationClient
{
    private readonly IApiClientFactory _factory;

    public DeviceTrustCompensationClient(IApiClientFactory factory) =>
        _factory = factory;

    public async Task UntrustAsync(
        AccessTokenBinding binding,
        string deviceId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        using var client = _factory.CreateAuthenticated(
            binding.Endpoints,
            () => binding,
            (_, _) => Task.FromResult(false));
        await client.UntrustDevicesAsync(
            new UntrustDevicesRequest([deviceId]),
            ct).ConfigureAwait(false);
    }
}
