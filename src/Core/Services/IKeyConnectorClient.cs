namespace Core.Services;

public interface IKeyConnectorClient
{
    Task<byte[]> GetMasterKeyAsync(
        string keyConnectorUrl,
        string accessToken,
        CancellationToken ct = default);
}
