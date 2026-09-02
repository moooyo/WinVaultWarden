namespace Api;

public interface IAuthenticatedApiClient :
    IReadonlyApiClient,
    IVaultWriteApiClient,
    ISendApiClient,
    IAttachmentApiClient,
    IAccountApiClient,
    ITwoFactorApiClient,
    IAuthRequestApiClient,
    IDeviceTrustApiClient,
    ITdeRegistrationApiClient,
    IEmergencyAccessApiClient,
    IOriginBoundApiClient,
    IDisposable
{
}
